using System.Management;
using System.Text.RegularExpressions;
using OpenPartition.Model;

namespace OpenPartition.Operations;

/// <summary>
/// When the engine can't lock a volume, name what is probably using it: running processes and
/// services whose program lives on that volume. It can't see programs elsewhere that only have
/// files open on it, so the message also says how to find those.
/// </summary>
public static partial class VolumeUsers
{
    [GeneratedRegex(@"could not lock (\\\\\?\\Volume\{[0-9a-fA-F-]+\}\\?)")]
    private static partial Regex LockFailure();

    /// <summary><paramref name="error"/> with the volume's drive letter and likely users added, if it is a lock failure.</summary>
    public static string Explain(string error, SystemSnapshot snapshot)
    {
        var match = LockFailure().Match(error);
        if (!match.Success)
        {
            return error;
        }
        var path = match.Groups[1].Value.TrimEnd('\\') + "\\";
        var volume = snapshot.Disks.SelectMany(d => d.Partitions).Select(p => p.Volume)
            .FirstOrDefault(v => v is not null && string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase));
        var name = volume?.DriveLetter is { } letter ? $"{letter}:" : volume?.Label is { Length: > 0 } label ? $"'{label}'" : path;
        var users = volume?.DriveLetter is { } l ? Find($"{l}:\\") : [];
        var found = users.Count == 0
            ? $"No running program or service is installed on {name}, so a program elsewhere has files open on it (a service writing logs or data there, a database, antivirus, or an Explorer window)."
            : $"Running from {name}: {string.Join(", ", users)}.";
        return $"{name} is in use, so it can't be locked for the move. {found} Stop those, then Apply again. To see every program with files open on it, use Resource Monitor (CPU tab, Associated Handles, search \"{(volume?.DriveLetter is { } d ? $"{d}:\\" : path)}\") or Sysinternals handle.exe. Nothing was changed by this step.";
    }

    private static List<string> Find(string root)
    {
        var users = new List<string>();
        try
        {
            var scope = new ManagementScope(@"root\cimv2");
            scope.Connect();
            using (var services = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, DisplayName, PathName, State FROM Win32_Service WHERE State = 'Running'")))
            {
                foreach (ManagementObject s in services.Get())
                {
                    using (s)
                    {
                        if (Unquote(s["PathName"] as string).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            users.Add($"service '{s["DisplayName"] ?? s["Name"]}' ({s["Name"]})");
                        }
                    }
                }
            }
            using var processes = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, ProcessId, ExecutablePath FROM Win32_Process"));
            foreach (ManagementObject p in processes.Get())
            {
                using (p)
                {
                    if ((p["ExecutablePath"] as string ?? "").StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        users.Add($"{p["Name"]} (process {p["ProcessId"]})");
                    }
                }
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Best effort: the error is still clear without the list.
        }
        return users.Distinct().Take(12).ToList();
    }

    private static string Unquote(string? command) =>
        command is null ? "" : command.TrimStart().StartsWith('"') ? command.TrimStart()[1..] : command.TrimStart();
}
