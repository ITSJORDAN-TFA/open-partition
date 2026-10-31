using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenPartition.Discovery;
using OpenPartition.Model;

namespace OpenPartition.Operations;

public static partial class ShadowCopies
{
    public const string Guidance = "Deletion permanently removes previous versions and restore points. Relocation is unsupported: Windows does not promise to preserve snapshots when storage is changed. For backup-managed copies or remaining storage associations, contact the backup administrator; moving stays refused until discovery is clear.";

    [GeneratedRegex(@"^\\\\\?\\Volume\{[0-9a-fA-F-]{36}\}\\$")]
    private static partial Regex VolumePattern();

    public static string[] DeleteArguments(string volume, string confirmation)
    {
        if (!VolumePattern().IsMatch(volume) || !Guid.TryParse(volume.AsSpan(11, 36), out _))
            throw new PlanException("Use the full volume GUID path shown by discovery.");
        if (!string.Equals(volume, confirmation, StringComparison.OrdinalIgnoreCase))
            throw new PlanException($"Nothing changed. To confirm permanent deletion, type the exact volume path: {volume}");
        return ["delete", "shadows", "/for=" + volume, "/all", "/quiet"];
    }

    public static string Explain(VolumeInfo volume) => string.Join(Environment.NewLine,
        volume.ShadowStorage.Select(a => $"Source {a.SourceVolume} stores shadow copies on {a.StorageVolume}.")
        .Prepend($"Shadow copies for {volume.Path}: {(volume.HasShadowCopies ? "present" : "none detected")}.")) + Environment.NewLine + Guidance;

    public static async Task DeleteAsync(string volume, string confirmation)
    {
        var arguments = DeleteArguments(volume, confirmation);
        var snapshot = await DiskDiscovery.CollectAsync();
        var owners = snapshot.Disks.Where(d => d.Partitions.Any(p => p.Volume?.Path.Equals(volume, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        // Restrict remediation to test disks. A source on a real disk can have storage on another disk.
        var affected = snapshot.Disks.Where(d => d.Partitions.Any(p => p.Volume is { } v &&
            (v.Path.Equals(volume, StringComparison.OrdinalIgnoreCase) || v.ShadowStorage.Any(a => a.SourceVolume.Equals(volume, StringComparison.OrdinalIgnoreCase))))).ToArray();
        var relations = affected.SelectMany(d => d.Partitions).Where(p => p.Volume is not null)
            .SelectMany(p => p.Volume!.ShadowStorage).Where(a => a.SourceVolume.Equals(volume, StringComparison.OrdinalIgnoreCase)).ToArray();
        var knownPaths = affected.SelectMany(d => d.Partitions).Where(p => p.Volume is not null).Select(p => p.Volume!.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (snapshot.Issues.Any(i => i.Message.Contains("Shadow", StringComparison.OrdinalIgnoreCase) && i.Code == "discovery.note") || relations.Any(a => !knownPaths.Contains(a.StorageVolume)))
            throw new PlanException("Nothing changed. Shadow storage could not be fully resolved. Refresh as Administrator or contact the backup administrator.");
        if (!snapshot.IsElevated || owners.Length != 1 || affected.Any(d => WriteGate.For(snapshot, d) != WriteVerdict.Allowed))
            throw new PlanException("Nothing changed. Automatic deletion requires Administrator and affected volumes on throwaway file-backed virtual disks. Ask your backup administrator to manage other volumes.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "vssadmin.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new PlanException("Could not start vssadmin; nothing changed.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new PlanException("Shadow deletion did not complete; some copies may remain. Refresh discovery and contact the backup administrator. No alternative deletion was attempted.");
    }
}
