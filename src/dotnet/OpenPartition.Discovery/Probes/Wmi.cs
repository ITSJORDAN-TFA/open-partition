using System.Management;

namespace OpenPartition.Discovery.Probes;

/// <summary>Thin, failure-tolerant WMI query helper. Discovery keeps going when a namespace is missing.</summary>
internal static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string BitLocker = @"root\cimv2\Security\MicrosoftVolumeEncryption";

    public sealed record Result(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, string? Error);

    public static Result Query(string scope, string wql)
    {
        try
        {
            var managementScope = new ManagementScope(scope);
            managementScope.Connect();
            using var searcher = new ManagementObjectSearcher(managementScope, new ObjectQuery(wql),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(30), ReturnImmediately = false });
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in item.Properties)
                    {
                        row[property.Name] = property.Value;
                    }
                    rows.Add(row);
                }
            }
            return new Result(rows, null);
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new Result([], e.Message.Trim());
        }
    }

    public static string? String(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is not null ? v.ToString()?.Trim() : null;

    public static bool Bool(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is bool b && b;

    public static long? Long(this IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is not null ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : null;
}
