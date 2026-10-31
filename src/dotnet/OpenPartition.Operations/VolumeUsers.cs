using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using OpenPartition.Model;

namespace OpenPartition.Operations;

/// <summary>A running service that holds the volume open, with the running services that depend on it.</summary>
public sealed record ServiceUser(string Name, string DisplayName, bool CanStop, IReadOnlyList<string> RunningDependents);

/// <summary>A process using the volume: files it has open there, the services it runs, or its program living there.</summary>
public sealed record VolumeUser(int ProcessId, string ProcessName, string? AppName, IReadOnlyList<ServiceUser> Services, IReadOnlyList<string> Files, bool RunsFromVolume = false)
{
    /// <summary>Process 4 holds files open for Windows itself: page files, network shares, drivers.</summary>
    public bool IsSystem => ProcessId == 4;

    /// <summary>One service in its own process: stopping it is sure to close its files.</summary>
    public ServiceUser? OwnService => Services.Count == 1 && !IsSystem ? Services[0] : null;
}

/// <summary>Why a volume could not be locked: who uses it, as far as could be found.</summary>
/// <param name="VolumeName">How the user knows the volume: <c>E:</c>, <c>'Apps'</c> or its GUID path.</param>
/// <param name="SearchPath">What to search for in Resource Monitor.</param>
/// <param name="Partial">Some processes could not be looked at, so the list may be incomplete.</param>
public sealed record LockDiagnosis(string VolumeName, string SearchPath, IReadOnlyList<VolumeUser> Users, bool Partial)
{
    /// <summary>Services that can be stopped to free the volume (shared service hosts and Windows itself excluded).</summary>
    public IReadOnlyList<ServiceUser> StoppableServices =>
        Users.Select(u => u.OwnService).OfType<ServiceUser>().Where(s => s.CanStop).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Whether the message points at the GUI's stop button or at a command.</summary>
public enum StopHint
{
    Command,
    Button,
}

/// <summary>
/// When the engine can't lock a volume, name what is using it: processes with files open on it
/// (found by a read-only scan of open handles, then Restart Manager for names and services),
/// plus services and programs that run from it. Processes are never stopped by this program;
/// services only when the user asks (see <see cref="ServiceControl"/>).
/// </summary>
public static partial class VolumeUsers
{
    private const int MaxListed = 12;

    [GeneratedRegex(@"could not lock (\\\\\?\\Volume\{[0-9a-fA-F-]+\}\\?)")]
    private static partial Regex LockFailure();

    /// <summary>The volume GUID path in a lock failure, or null for any other error.</summary>
    public static string? LockedVolume(string error)
    {
        var match = LockFailure().Match(error);
        return match.Success ? match.Groups[1].Value.TrimEnd('\\') + "\\" : null;
    }

    /// <summary><paramref name="error"/> with the volume's name and its users added, if it is a lock failure.</summary>
    public static string Explain(string error, SystemSnapshot snapshot, StopHint hint = StopHint.Command) =>
        Diagnose(error, snapshot) is { } diagnosis ? Format(diagnosis, hint) : error;

    /// <summary>Who uses the volume in a lock failure, or null when <paramref name="error"/> is something else. Takes a few seconds.</summary>
    public static LockDiagnosis? Diagnose(string error, SystemSnapshot snapshot)
    {
        if (LockedVolume(error) is not { } path)
        {
            return null;
        }
        var volume = snapshot.Disks.SelectMany(d => d.Partitions).Select(p => p.Volume)
            .FirstOrDefault(v => v is not null && string.Equals(v.Path, path, StringComparison.OrdinalIgnoreCase));
        var name = volume?.DriveLetter is { } letter ? $"{letter}:" : volume?.Label is { Length: > 0 } label ? $"'{label}'" : path;
        var root = volume?.DriveLetter is { } l ? $"{l}:\\" : path;
        var (users, partial) = Find(path, root);
        return new LockDiagnosis(name, root, users, partial);
    }

    /// <summary>The message for a lock failure. Pure: no system calls.</summary>
    public static string Format(LockDiagnosis d, StopHint hint)
    {
        var text = new StringBuilder($"{d.VolumeName} is in use, so it can't be locked for the move. Nothing was changed by this step.");
        if (d.Users.Count == 0)
        {
            text.Append(d.Partial
                ? $" No program could be found using it, but not every program could be checked{(Environment.IsPrivilegedProcess ? "" : " (run as Administrator to check them all)")}."
                : " No program was found with files open on it; it may have been a short-lived use (antivirus, the indexer, an Explorer window). Try again.");
        }
        else
        {
            text.Append("\nUsing it:");
            foreach (var user in d.Users.Take(MaxListed))
            {
                text.Append("\n  - ").Append(Describe(user, d.VolumeName));
            }
            if (d.Users.Count > MaxListed)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n  - and {d.Users.Count - MaxListed} more");
            }
            text.Append('\n').Append(NextSteps(d, hint));
            if (d.Partial)
            {
                text.Append(" Some programs could not be checked, so this list may be incomplete.");
            }
        }
        if (d.Users.Count == 0 || d.Partial)
        {
            text.Append(CultureInfo.InvariantCulture, $" To see every program with files open on it, use Resource Monitor (CPU tab, Associated Handles, search \"{d.SearchPath}\") or Sysinternals handle.exe.");
        }
        return text.ToString();
    }

    /// <summary>Label of the GUI button that stops <paramref name="services"/> and retries.</summary>
    public static string StopButtonText(IReadOnlyList<ServiceUser> services) =>
        services.Count == 1 ? $"Stop service {services[0].DisplayName} and retry" : $"Stop {services.Count} services and retry";

    /// <summary>The question asked before stopping <paramref name="services"/>.</summary>
    public static string StopConfirmation(IReadOnlyList<ServiceUser> services)
    {
        var text = new StringBuilder(services.Count == 1 ? "Stop this service?\n" : "Stop these services?\n");
        foreach (var s in services)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n  - {s.DisplayName} ({s.Name})");
            if (s.RunningDependents.Count > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"\n    also stops the services that depend on it: {string.Join(", ", s.RunningDependents)}");
            }
        }
        text.Append("\n\nWhatever relies on them stops working until they are started again. They are not started again automatically: "
            + "start them from Services (services.msc) or with Start-Service when the move is done. "
            + "No program is closed; close any others yourself.");
        return text.ToString();
    }

    private static string Describe(VolumeUser u, string volumeName)
    {
        var exe = $"{u.ProcessName} (process {u.ProcessId})";
        var who = u.IsSystem
            ? "Windows itself (System, process 4): a page file, a folder shared over the network, or a driver"
            : u.OwnService is { } s ? $"service '{s.DisplayName}' ({s.Name}), in {exe}"
            : u.Services.Count > 1 ? $"{exe}, which runs the services {string.Join(", ", u.Services.Select(x => x.Name))}"
            : u.AppName is { Length: > 0 } app && !string.Equals(Path.GetFileNameWithoutExtension(u.ProcessName), app, StringComparison.OrdinalIgnoreCase)
                ? $"{app} ({u.ProcessName}, process {u.ProcessId})"
            : exe;
        var what = u.Files.Count switch
        {
            0 when u.RunsFromVolume => $"runs from {volumeName}",
            0 => "has files open there",
            1 => u.Files[0],
            2 => $"{u.Files[0]} and 1 more file",
            _ => $"{u.Files[0]} and {u.Files.Count - 1} more files",
        };
        return $"{who}: {what}";
    }

    private static string NextSteps(LockDiagnosis d, StopHint hint)
    {
        var stoppable = d.StoppableServices;
        var others = d.Users.Where(u => u.OwnService is not { CanStop: true }).ToList();
        var steps = new List<string>();
        if (stoppable.Count > 0)
        {
            var dependents = stoppable.SelectMany(s => s.RunningDependents).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            steps.Add(hint == StopHint.Button
                ? $"Use \"{StopButtonText(stoppable)}\" below to stop {(stoppable.Count == 1 ? "the service" : "those services")}"
                : $"Stop {(stoppable.Count == 1 ? "the service" : "the services")} (Stop-Service -Name {string.Join(", ", stoppable.Select(s => s.Name))}"
                    + (dependents.Count > 0 ? $" -Force; -Force also stops {string.Join(", ", dependents)}, which depend on {(stoppable.Count == 1 ? "it" : "them")}" : "") + ")");
        }
        if (others.Any(u => u.IsSystem))
        {
            steps.Add("close any network sessions to its shared folders (Computer Management, Shared Folders, Open Files) and move any page file off it");
        }
        if (others.Any(u => !u.IsSystem && u.Services.Count > 1))
        {
            steps.Add("stop the service in a shared host that uses it (Services, services.msc)");
        }
        if (others.Any(u => !u.IsSystem && u.Services.Count <= 1))
        {
            steps.Add("close the other programs listed (this program never closes programs itself)");
        }
        var sentence = string.Join(", ", steps.Take(steps.Count - 1)) + (steps.Count > 1 ? " and " : "") + steps[^1];
        return $"{char.ToUpperInvariant(sentence[0])}{sentence[1..]}, then try again.";
    }

    private static (List<VolumeUser> Users, bool Partial) Find(string volumePath, string root)
    {
        var partial = false;
        OpenFileScan scan;
        try
        {
            scan = OpenFiles.OnVolume(volumePath, root, TimeSpan.FromSeconds(20));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or OverflowException)
        {
            scan = new OpenFileScan([], Partial: true);
        }
        partial |= scan.Partial;
        var files = scan.Holders.ToDictionary(h => h.ProcessId, h => h.Files);

        // Restart Manager adds friendly names and service names, and other processes using the same files.
        var restart = OpenFiles.RestartManager(scan.Holders.SelectMany(h => h.Files).Distinct(StringComparer.OrdinalIgnoreCase).Take(1000).ToList())
            .GroupBy(r => r.ProcessId).ToDictionary(g => g.Key, g => g.First());
        var pids = new HashSet<int>(files.Keys.Concat(restart.Keys));

        var services = new Dictionary<int, List<ServiceUser>>();
        var fromVolume = new HashSet<int>();
        try
        {
            var scope = new ManagementScope(@"root\cimv2");
            scope.Connect();
            using (var running = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name, DisplayName, PathName, ProcessId, AcceptStop FROM Win32_Service WHERE State = 'Running'")))
            {
                var all = new List<(int Pid, string Name, string Display, bool CanStop, string Path)>();
                foreach (ManagementObject s in running.Get())
                {
                    using (s)
                    {
                        all.Add((Convert.ToInt32(s["ProcessId"] ?? 0, CultureInfo.InvariantCulture), s["Name"] as string ?? "", s["DisplayName"] as string ?? s["Name"] as string ?? "",
                            s["AcceptStop"] is true, Unquote(s["PathName"] as string)));
                    }
                }
                foreach (var s in all.Where(s => s.Pid > 0 && s.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                {
                    pids.Add(s.Pid);
                    fromVolume.Add(s.Pid);
                }
                foreach (var s in all.Where(s => pids.Contains(s.Pid)))
                {
                    if (!services.TryGetValue(s.Pid, out var list))
                    {
                        services[s.Pid] = list = [];
                    }
                    list.Add(new ServiceUser(s.Name, s.Display, s.CanStop, ServiceControl.RunningDependents(scope, s.Name)));
                }
            }
            using var processes = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT ProcessId, ExecutablePath FROM Win32_Process"));
            foreach (ManagementObject p in processes.Get())
            {
                using (p)
                {
                    if ((p["ExecutablePath"] as string ?? "").StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        var pid = Convert.ToInt32(p["ProcessId"], CultureInfo.InvariantCulture);
                        pids.Add(pid);
                        fromVolume.Add(pid);
                    }
                }
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Best effort: the processes are still named without their services.
            partial = true;
        }

        var users = pids.Where(p => p != 0).Order().Select(pid => new VolumeUser(
            pid,
            ProcessName(pid),
            restart.TryGetValue(pid, out var r) && r.AppName.Length > 0 ? r.AppName : null,
            services.GetValueOrDefault(pid) ?? [],
            files.GetValueOrDefault(pid) ?? [],
            fromVolume.Contains(pid))).ToList();
        return (users, partial);
    }

    private static string ProcessName(int pid)
    {
        if (pid == 4)
        {
            return "System";
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return "a program that has since exited";
        }
    }

    private static string Unquote(string? command) =>
        command is null ? "" : command.TrimStart().StartsWith('"') ? command.TrimStart()[1..] : command.TrimStart();
}
