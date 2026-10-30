using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using OpenPartition.Discovery.Native;
using OpenPartition.Model;

namespace OpenPartition.Discovery.Probes;

/// <summary>Facts about what a volume is being used for: page and hibernation files, BitLocker, VSS.</summary>
internal static partial class VolumeUsageProbe
{
    public sealed record BitLockerInfo(BitLockerState State, string? Detail);

    [GeneratedRegex(@"Volume\{[0-9a-fA-F-]{36}\}")]
    private static partial Regex VolumeGuidPattern();

    /// <summary>Volume paths that hold a page file in use, or a pagefile.sys/swapfile.sys file.</summary>
    public static HashSet<string> PageFileVolumes(IEnumerable<VolumeFacts> volumes, List<string> notes)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usage = Wmi.Query(Wmi.Cimv2, "SELECT Name FROM Win32_PageFileUsage");
        if (usage.Error is not null)
        {
            notes.Add($"Page file query failed: {usage.Error}");
        }
        foreach (var row in usage.Rows)
        {
            if (row.String("Name") is { } name && VolumeProbe.VolumeOf(name) is { } volume)
            {
                result.Add(volume);
            }
        }
        // Belt and braces: a page file configured for the next boot, or one WMI missed.
        foreach (var v in volumes)
        {
            if (File.Exists(v.Path + "pagefile.sys") || File.Exists(v.Path + "swapfile.sys"))
            {
                result.Add(v.Path);
            }
        }
        return result;
    }

    public static HashSet<string> HibernationVolumes(IEnumerable<VolumeFacts> volumes) =>
        volumes.Where(v => File.Exists(v.Path + "hiberfil.sys")).Select(v => v.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static (HashSet<string> WithShadowCopies, HashSet<string> HostingShadowStorage) ShadowCopies(List<string> notes)
    {
        var copies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shadowCopies = Wmi.Query(Wmi.Cimv2, "SELECT VolumeName FROM Win32_ShadowCopy");
        if (shadowCopies.Error is not null)
        {
            notes.Add($"Shadow copy query failed: {shadowCopies.Error}");
        }
        foreach (var row in shadowCopies.Rows)
        {
            if (row.String("VolumeName") is { } name)
            {
                copies.Add(VolumeProbe.Normalize(name));
            }
        }
        var shadowStorage = Wmi.Query(Wmi.Cimv2, "SELECT Volume, DiffVolume FROM Win32_ShadowStorage");
        if (shadowStorage.Error is not null)
        {
            notes.Add($"Shadow storage query failed: {shadowStorage.Error}");
        }
        foreach (var row in shadowStorage.Rows)
        {
            // References look like: Win32_Volume.DeviceID="\\\\?\\Volume{guid}\\"
            if (row.String("DiffVolume") is { } diff && VolumeGuidPattern().Match(diff) is { Success: true } m)
            {
                storage.Add(VolumeProbe.Normalize($@"\\?\{m.Value}"));
            }
        }
        return (copies, storage);
    }

    public static Dictionary<string, BitLockerInfo> BitLocker(IEnumerable<VolumeFacts> volumes, bool elevated, List<string> notes)
    {
        var result = new Dictionary<string, BitLockerInfo>(StringComparer.OrdinalIgnoreCase);
        if (!elevated)
        {
            foreach (var v in volumes)
            {
                result[v.Path] = new BitLockerInfo(BitLockerState.Unknown, "needs Administrator");
            }
            return result;
        }

        var query = Wmi.Query(Wmi.BitLocker, "SELECT DeviceID, ConversionStatus, ProtectionStatus FROM Win32_EncryptableVolume");
        if (query.Error is null)
        {
            foreach (var row in query.Rows)
            {
                if (row.String("DeviceID") is not { } id)
                {
                    continue;
                }
                var conversion = row.Long("ConversionStatus");
                var protection = row.Long("ProtectionStatus");
                result[VolumeProbe.Normalize(id)] = conversion switch
                {
                    0 => new BitLockerInfo(BitLockerState.NotEncrypted, null),
                    1 => new BitLockerInfo(BitLockerState.Encrypted, protection == 1 ? "fully encrypted, protection on" : "fully encrypted, protection suspended"),
                    2 => new BitLockerInfo(BitLockerState.Encrypted, "encryption in progress"),
                    3 => new BitLockerInfo(BitLockerState.Encrypted, "decryption in progress"),
                    4 => new BitLockerInfo(BitLockerState.Encrypted, "encryption paused"),
                    5 => new BitLockerInfo(BitLockerState.Encrypted, "decryption paused"),
                    _ => new BitLockerInfo(BitLockerState.Unknown, $"unrecognised conversion status {conversion}"),
                };
            }
        }
        else
        {
            notes.Add($"BitLocker WMI provider unavailable ({query.Error}); checking volume boot sectors instead.");
        }

        // Volumes the provider didn't report (or no provider at all): look for the BitLocker
        // signature in the volume boot sector. Read-only access.
        foreach (var v in volumes.Where(v => !result.ContainsKey(v.Path)))
        {
            result[v.Path] = v.Extents.Count == 1
                ? BootSectorCheck(v.Extents[0].Disk, v.Extents[0].Offset)
                : new BitLockerInfo(BitLockerState.Unknown, "volume has no single disk extent");
        }
        return result;
    }

    /// <summary>
    /// Reads the partition's first sector from the physical disk. The volume device can't be
    /// used: on an unlocked BitLocker volume it shows the decrypted NTFS boot sector.
    /// </summary>
    private static BitLockerInfo BootSectorCheck(int disk, long offset)
    {
        try
        {
            using var handle = DeviceHandle.OpenRead($@"\\.\PhysicalDrive{disk}");
            var sector = DeviceHandle.ReadAt(handle, offset, 4096);
            var oem = sector.Length >= 11 ? Encoding.ASCII.GetString(sector, 3, 8) : "";
            return oem == "-FVE-FS-"
                ? new BitLockerInfo(BitLockerState.Encrypted, "BitLocker signature found in boot sector")
                : new BitLockerInfo(BitLockerState.NotEncrypted, null);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new BitLockerInfo(BitLockerState.Unknown, e.Message);
        }
    }
}
