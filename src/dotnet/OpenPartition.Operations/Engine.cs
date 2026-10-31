using System.Diagnostics;
using System.Globalization;

namespace OpenPartition.Operations;

/// <summary>Progress of a running step: 0..1 when known, plus a line of text.</summary>
public sealed record StepProgress(double? Fraction, string Text);

public sealed class OperationFailedException(string message) : Exception(message);

/// <summary>An interrupted move found in the journal folder.</summary>
public sealed record PendingMove(string Journal, string DiskId, long DiskSize, long Source, long Target, long Length, string Chunks)
{
    /// <summary>
    /// Whether the move can be undone: source and target don't overlap, so the original location
    /// was never written and the partition can simply be put back there.
    /// </summary>
    public bool CanUndo => Math.Abs(Target - Source) >= Length;
}

/// <summary>
/// Runs <c>opm-engine.exe</c>, the Rust engine that does the journaled, crash-safe move. Each move
/// gets its own journal file under %ProgramData%\OpenPartition\journals, which recovery reads.
/// </summary>
public static class Engine
{
    public static string JournalFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenPartition", "journals");

    /// <summary>opm-engine.exe next to this program, or <c>OPM_ENGINE_EXE</c> for development.</summary>
    public static string ExecutablePath
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("OPM_ENGINE_EXE");
            if (!string.IsNullOrEmpty(overridden) && File.Exists(overridden))
            {
                return overridden;
            }
            var local = Path.Combine(AppContext.BaseDirectory, "opm-engine.exe");
            return File.Exists(local) ? local : throw new OperationFailedException($"opm-engine.exe was not found next to {AppContext.BaseDirectory}.");
        }
    }

    public static async Task MoveAsync(MoveStep step, string? snapshot, IProgress<StepProgress>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(JournalFolder);
        var journal = Path.Combine(JournalFolder, $"move-{DateTime.UtcNow:yyyyMMdd-HHmmss}-disk{step.DiskNumber}.journal");
        var args = new List<string>
        {
            "move", "--disk", Num(step.DiskNumber), "--from", Num(step.From), "--to", Num(step.To), "--journal", journal,
        };
        await RunAsync(args, snapshot, journal, progress, cancellationToken).ConfigureAwait(false);
        Retire(journal);
    }

    /// <summary>Grow an MBR extended partition: one partition table write, no data moves.</summary>
    public static async Task GrowExtendedAsync(ExtendStep step, string? snapshot, IProgress<StepProgress>? progress, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(JournalFolder);
        var log = Path.Combine(JournalFolder, "done", $"grow-{DateTime.UtcNow:yyyyMMdd-HHmmss}-disk{step.DiskNumber}.journal");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        progress?.Report(new StepProgress(null, "Growing the extended partition (partition table only)"));
        var args = new List<string>
        {
            "grow-extended", "--disk", Num(step.DiskNumber), "--offset", Num(step.Offset), "--length", Num(step.NewSize),
        };
        await RunAsync(args, snapshot, log, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new StepProgress(1, "Grown"));
    }

    public static async Task RecoverAsync(PendingMove pending, int diskNumber, string? snapshot, IProgress<StepProgress>? progress, CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "recover", "--disk", Num(diskNumber), "--journal", pending.Journal };
        await RunAsync(args, snapshot, pending.Journal, progress, cancellationToken).ConfigureAwait(false);
        Retire(pending.Journal);
    }

    /// <summary>Undo an interrupted move: put the partition back where it was. One table write.</summary>
    public static async Task UndoAsync(PendingMove pending, int diskNumber, string? snapshot, IProgress<StepProgress>? progress, CancellationToken cancellationToken = default)
    {
        progress?.Report(new StepProgress(null, "Putting the partition back where it was"));
        var args = new List<string> { "rollback", "--disk", Num(diskNumber), "--journal", pending.Journal };
        await RunAsync(args, snapshot, pending.Journal, progress, cancellationToken).ConfigureAwait(false);
        Retire(pending.Journal);
        progress?.Report(new StepProgress(1, "The partition is back where it was."));
    }

    /// <summary>Unfinished moves, oldest first. A finished journal is never listed.</summary>
    public static async Task<IReadOnlyList<PendingMove>> FindPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(JournalFolder))
        {
            return [];
        }
        var pending = new List<PendingMove>();
        foreach (var journal in Directory.GetFiles(JournalFolder, "*.journal").Order(StringComparer.Ordinal))
        {
            var result = await ExecAsync(["status", "--journal", journal], null, cancellationToken).ConfigureAwait(false);
            var values = Parse(result.Lines);
            if (values.GetValueOrDefault("status") != "unfinished")
            {
                continue;
            }
            pending.Add(new PendingMove(journal, values["disk_id"], Long(values["disk_size"]), Long(values["source"]),
                Long(values["target"]), Long(values["length"]), values.GetValueOrDefault("chunks") ?? "?"));
        }
        return pending;
    }

    /// <summary>The engine's disk identity (GPT disk GUID bytes, or MBR signature + "MBR") as upper-case hex.</summary>
    public static string DiskIdOf(Model.DiskInfo disk)
    {
        var id = new byte[16];
        if (disk.GptDiskId is { } guid)
        {
            id = guid.ToByteArray();
        }
        else if (disk.MbrSignature is { } signature)
        {
            BitConverter.GetBytes(signature).CopyTo(id, 0);
            "MBR"u8.ToArray().CopyTo(id, 4);
        }
        return Convert.ToHexString(id);
    }

    private static async Task RunAsync(List<string> args, string? snapshot, string journal, IProgress<StepProgress>? progress, CancellationToken cancellationToken)
    {
        var log = Path.ChangeExtension(journal, ".log");
        var result = await ExecAsync(args, snapshot, cancellationToken, line =>
        {
            File.AppendAllLines(log, [line]);
            if (line.StartsWith("progress ", StringComparison.Ordinal))
            {
                var values = Parse([line["progress ".Length..]]);
                if (values.TryGetValue("bytes", out var bytes) && bytes.Split('/') is [var done, var total] && Long(total) > 0)
                {
                    progress?.Report(new StepProgress((double)Long(done) / Long(total), ProgressText(values.GetValueOrDefault("stage"), Long(done), Long(total))));
                }
            }
            else if (line.StartsWith("locked=", StringComparison.Ordinal))
            {
                progress?.Report(new StepProgress(null, "Volumes locked"));
            }
            else if (line.StartsWith("moving ", StringComparison.Ordinal)
                && Parse([line]) is var moving
                && moving.TryGetValue("copy_bytes", out var copy) && moving.TryGetValue("length", out var length)
                && Long(copy) < Long(length))
            {
                progress?.Report(new StepProgress(null,
                    $"Only the {Model.Units.FormatBytes(Long(copy))} that hold data are copied; the {Model.Units.FormatBytes(Long(length) - Long(copy))} of free space is skipped"));
            }
        }).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            if (result.Lines.Contains("rolled_back=yes"))
            {
                Retire(journal);
            }
            var error = Parse(result.Lines).GetValueOrDefault("error")
                ?? $"opm-engine stopped unexpectedly (exit code {result.ExitCode}). The move is journaled and nothing is lost: finish it with Resume in the GUI or 'opm resume'.";
            throw new OperationFailedException(error);
        }
    }

    private static string ProgressText(string? stage, long done, long total) => stage switch
    {
        "hashing" => $"Fingerprinting the data before copying: {Model.Units.FormatBytes(done)} of {Model.Units.FormatBytes(total)}",
        "verifying" => $"Checking the copy: {Model.Units.FormatBytes(done)} of {Model.Units.FormatBytes(total)}",
        _ => $"Copied {Model.Units.FormatBytes(done)} of {Model.Units.FormatBytes(total)}",
    };

    /// <summary>Keep a finished journal's log and copy map for the record, but stop listing the journal.</summary>
    private static void Retire(string journal)
    {
        var done = Path.Combine(JournalFolder, "done");
        Directory.CreateDirectory(done);
        File.Move(journal, Path.Combine(done, Path.GetFileName(journal)), overwrite: true);
        var map = journal + ".map";
        if (File.Exists(map))
        {
            File.Move(map, Path.Combine(done, Path.GetFileName(map)), overwrite: true);
        }
    }

    private sealed record ExecResult(int ExitCode, IReadOnlyList<string> Lines);

    private static async Task<ExecResult> ExecAsync(List<string> args, string? snapshot, CancellationToken cancellationToken, Action<string>? onLine = null)
    {
        var start = new ProcessStartInfo(ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }
        // Test hook: slow every chunk down so a harness test can interrupt a move part way.
        if (args[0] is "move" or "recover" && Environment.GetEnvironmentVariable("OPM_ENGINE_DELAY_MS") is { Length: > 0 } delay)
        {
            start.ArgumentList.Add("--delay-ms");
            start.ArgumentList.Add(delay);
        }
        if (!string.IsNullOrWhiteSpace(snapshot))
        {
            start.ArgumentList.Add("--snapshot-confirmed");
            start.ArgumentList.Add(snapshot);
        }
        using var process = Process.Start(start) ?? throw new OperationFailedException("opm-engine could not be started.");
        var lines = new List<string>();
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lines.Add(line);
            onLine?.Invoke(line);
        }
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var errors = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0 && !lines.Any(l => l.StartsWith("error=", StringComparison.Ordinal)) && errors.Length > 0)
        {
            lines.Add("error=" + errors.Trim());
        }
        return new ExecResult(process.ExitCode, lines);
    }

    private static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            foreach (var part in line.Split(' '))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0 && !line.StartsWith("error=", StringComparison.Ordinal))
                {
                    values[part[..eq]] = part[(eq + 1)..];
                }
            }
            if (line.StartsWith("error=", StringComparison.Ordinal))
            {
                values["error"] = line["error=".Length..];
            }
        }
        return values;
    }

    private static long Long(string s) => long.Parse(s, CultureInfo.InvariantCulture);

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);
}
