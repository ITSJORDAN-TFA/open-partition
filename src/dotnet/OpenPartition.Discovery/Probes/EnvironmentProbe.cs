using System.Net;
using System.Text.Json;
using OpenPartition.Model;

namespace OpenPartition.Discovery.Probes;

internal static class EnvironmentProbe
{
    /// <summary>SMBIOS chassis asset tag Azure sets on every VM.</summary>
    internal const string AzureAssetTag = "7783-7084-3265-9085-8269-3286-77";

    internal const string AzureTemporaryLabel = "Temporary Storage";
    internal const string AzureDataLossWarningFile = "DATALOSS_WARNING_README.txt";

    public static HostEnvironment Detect(List<string> notes)
    {
        var enclosure = Wmi.Query(Wmi.Cimv2, "SELECT SMBIOSAssetTag FROM Win32_SystemEnclosure");
        if (enclosure.Rows.Any(r => string.Equals(r.String("SMBIOSAssetTag"), AzureAssetTag, StringComparison.Ordinal)))
        {
            return HostEnvironment.Azure;
        }
        var system = Wmi.Query(Wmi.Cimv2, "SELECT Manufacturer, Model FROM Win32_ComputerSystem");
        if (system.Error is not null)
        {
            notes.Add($"Could not read the computer model: {system.Error}");
            return HostEnvironment.Unknown;
        }
        var row = system.Rows.Count > 0 ? system.Rows[0] : null;
        var manufacturer = row?.String("Manufacturer") ?? "";
        var model = row?.String("Model") ?? "";
        return (manufacturer, model) switch
        {
            ("Microsoft Corporation", "Virtual Machine") => HostEnvironment.HyperV,
            _ when model.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                || model.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                || model.Contains("KVM", StringComparison.OrdinalIgnoreCase)
                || manufacturer.Contains("QEMU", StringComparison.OrdinalIgnoreCase)
                || manufacturer.Contains("Xen", StringComparison.OrdinalIgnoreCase)
                || manufacturer.Contains("Amazon EC2", StringComparison.OrdinalIgnoreCase) => HostEnvironment.OtherVirtual,
            _ => HostEnvironment.Physical,
        };
    }

    /// <summary>Instance metadata from Azure IMDS. Only called once SMBIOS says we're on Azure.</summary>
    public static async Task<AzureInfo?> AzureMetadataAsync(List<string> notes, CancellationToken cancellationToken = default)
    {
        try
        {
            using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01"));
            request.Headers.Add("Metadata", "true");
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                notes.Add($"Azure metadata service returned {(int)response.StatusCode}.");
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            string? Get(string name) => json.RootElement.TryGetProperty(name, out var p) ? p.GetString() : null;
            return new AzureInfo
            {
                VmId = Get("vmId"),
                VmName = Get("name"),
                VmSize = Get("vmSize"),
                Location = Get("location"),
                ResourceGroup = Get("resourceGroupName"),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            notes.Add($"Azure metadata service unreachable: {e.Message}");
            return null;
        }
    }

    /// <summary>Heuristic for the Azure temporary (resource) disk volume.</summary>
    public static bool IsAzureTemporaryVolume(VolumeFacts volume) =>
        string.Equals(volume.Label, AzureTemporaryLabel, StringComparison.OrdinalIgnoreCase)
        || File.Exists(volume.Path + AzureDataLossWarningFile);
}
