using System.Globalization;
using OpenPartition.Model;

namespace OpenPartition.Cli;

/// <summary>Plain-text rendering of a discovery snapshot for the console.</summary>
internal static class TextReport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Write(TextWriter o, SystemSnapshot snapshot, bool detail)
    {
        o.WriteLine($"{ProductInfo.Name} {ProductInfo.Version} - read-only preview");
        o.WriteLine($"{snapshot.MachineName}  {snapshot.OsVersion}  environment: {EnvironmentText(snapshot)}{(snapshot.IsElevated ? "" : "  (not elevated)")}");
        foreach (var issue in snapshot.Issues.Where(i => i.Severity != IssueSeverity.Info || detail))
        {
            o.WriteLine($"  {Marker(issue.Severity)} {issue.Message}");
        }

        foreach (var disk in snapshot.Disks)
        {
            o.WriteLine();
            WriteDisk(o, disk, detail);
        }
    }

    private static string EnvironmentText(SystemSnapshot s) => s.Azure is { } a
        ? $"Azure ({string.Join(", ", new[] { a.VmName, a.VmSize, a.Location }.Where(x => !string.IsNullOrEmpty(x)))})"
        : s.Environment.ToString();

    internal static void WriteDisk(TextWriter o, DiskInfo disk, bool detail)
    {
        var flags = new List<string>();
        if (disk.IsBoot) flags.Add("boot");
        if (disk.IsSystem) flags.Add("system");
        if (disk.IsOffline) flags.Add("offline");
        if (disk.IsReadOnly) flags.Add("read-only");
        if (disk.IsAzureResourceDisk) flags.Add("AZURE TEMPORARY DISK");
        var sectors = disk.PhysicalSectorSize is { } ps && ps != disk.LogicalSectorSize
            ? $"{disk.LogicalSectorSize}/{ps}"
            : disk.LogicalSectorSize.ToString(Invariant);
        o.WriteLine($"Disk {disk.Number}  {disk.FriendlyName ?? "(unknown model)"}  {Units.FormatBytes(disk.Size)}  {disk.Style.ToString().ToUpperInvariant()}  sectors {sectors}  {disk.BusType}{(flags.Count > 0 ? "  [" + string.Join(", ", flags) + "]" : "")}");
        if (detail)
        {
            if (disk.GptDiskId is { } id) o.WriteLine($"  GPT disk ID {id:B}");
            if (disk.MbrSignature is { } sig) o.WriteLine($"  MBR signature 0x{sig:X8}");
            o.WriteLine($"  Usable area {Units.FormatBytes(disk.UsableStart)} to {Units.FormatBytes(disk.UsableEnd)}");
        }
        foreach (var issue in disk.Issues)
        {
            o.WriteLine($"  {Marker(issue.Severity)} {issue.Message}");
        }

        o.WriteLine($"  {"#",-3} {"Start",10} {"Size",10}  {"Type",-10} {"Vol",-4} {"FS",-6} {"Label",-16} {"Used",7}  Notes");
        var rows = disk.Partitions.Select(p => (p.Offset, Partition: (PartitionInfo?)p, Free: (FreeExtent?)null))
            .Concat(disk.FreeExtents.Select(f => (f.Offset, Partition: (PartitionInfo?)null, Free: (FreeExtent?)f)))
            .OrderBy(r => r.Offset);
        foreach (var row in rows)
        {
            if (row.Free is { } free)
            {
                o.WriteLine($"  {"",-3} {Units.FormatBytes(free.Offset),10} {Units.FormatBytes(free.Size),10}  {"(free)",-10}");
                continue;
            }
            var p = row.Partition!;
            var v = p.Volume;
            var used = v is { Capacity: > 0 and var cap, FreeSpace: { } fs } ? $"{(cap - fs) * 100.0 / cap,6:0}%" : "";
            var notes = string.Join(", ", ShortNotes(p));
            o.WriteLine($"  {p.Number,-3} {Units.FormatBytes(p.Offset),10} {Units.FormatBytes(p.Size),10}  {RoleText(p),-10} {(v?.DriveLetter is { } l ? l + ":" : ""),-4} {v?.FileSystem ?? "",-6} {Truncate(v?.Label, 16),-16} {used,7}  {notes}");
            if (detail)
            {
                WritePartitionDetail(o, disk, p);
            }
        }
    }

    private static void WritePartitionDetail(TextWriter o, DiskInfo disk, PartitionInfo p)
    {
        const string pad = "        ";
        o.WriteLine($"{pad}offset {p.Offset.ToString("N0", Invariant)} bytes, size {p.Size.ToString("N0", Invariant)} bytes");
        if (p.PartitionGuid is { } guid) o.WriteLine($"{pad}partition GUID {guid:B}  type {p.GptType:B}{(p.GptName is null ? "" : $"  name \"{p.GptName}\"")}");
        if (p.GptAttributes != 0) o.WriteLine($"{pad}GPT attributes 0x{p.GptAttributes:X16}");
        if (p.MbrType is { } mbr) o.WriteLine($"{pad}MBR type 0x{mbr:X2}{(p.IsActive ? "  active" : "")}{(p.IsLogical ? "  logical" : "")}");
        if (p.Volume is { } v)
        {
            o.WriteLine($"{pad}volume {v.Path}");
            foreach (var mount in v.MountPoints) o.WriteLine($"{pad}mounted at {mount}");
            o.WriteLine($"{pad}BitLocker: {v.BitLocker}{(v.BitLockerDetail is null ? "" : $" ({v.BitLockerDetail})")}");
        }
        foreach (var issue in p.Issues)
        {
            o.WriteLine($"{pad}{Marker(issue.Severity)} {issue.Message}");
        }
        var allowed = Enum.GetValues<OperationKind>()
            .Where(k => k is not (OperationKind.None or OperationKind.All or OperationKind.Create))
            .Where(k => disk.Allows(p, k))
            .Select(k => k.ToString());
        o.WriteLine($"{pad}allowed later: {string.Join(", ", allowed.DefaultIfEmpty("nothing"))}");
    }

    private static IEnumerable<string> ShortNotes(PartitionInfo p)
    {
        if (p.IsBoot) yield return "Windows";
        if (p.IsSystem) yield return "system";
        if (p.Volume is { } v)
        {
            if (v.HasPageFile) yield return "page file";
            if (v.HasHibernationFile) yield return "hiberfil";
            if (v.BitLocker == BitLockerState.Encrypted) yield return "BitLocker";
            if (v.HasShadowCopies) yield return "shadow copies";
            if (v.IsAzureTemporaryStorage) yield return "AZURE TEMP";
        }
        var blockers = p.Issues.Count(i => i.Severity == IssueSeverity.Blocker);
        if (blockers > 0) yield return $"{blockers} refusal{(blockers == 1 ? "" : "s")}";
    }

    private static string RoleText(PartitionInfo p) => p.Role switch
    {
        PartitionRole.Data => p.IsLogical ? "Logical" : "Data",
        PartitionRole.EfiSystem => "EFI",
        PartitionRole.MicrosoftReserved => "MSR",
        PartitionRole.Recovery => "Recovery",
        PartitionRole.DynamicData or PartitionRole.DynamicMetadata => "Dynamic",
        PartitionRole.StorageSpaces => "Spaces",
        PartitionRole.StorageReplica => "Replica",
        PartitionRole.Extended => "Extended",
        PartitionRole.MoveInProgress => "MOVING",
        _ => "Unknown",
    };

    private static string Marker(IssueSeverity severity) => severity switch
    {
        IssueSeverity.Blocker => "[refused]",
        IssueSeverity.Warning => "[warning]",
        _ => "[info]",
    };

    private static string Truncate(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..(max - 1)] + "~";
}
