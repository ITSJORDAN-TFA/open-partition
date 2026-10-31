using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPartition.Cli;
using OpenPartition.Discovery;
using OpenPartition.Model;
using OpenPartition.Operations;

string[] valued = ["--to", "--size", "--snapshot"];
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var positionalList = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (valued.Contains(args[i], StringComparer.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        options[args[i]] = args[++i];
    }
    else if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        options[args[i]] = "";
    }
    else
    {
        positionalList.Add(args[i]);
    }
}
var positional = positionalList.ToArray();
var json = options.ContainsKey("--json");
var yes = options.ContainsKey("--yes");
var command = positional.Length > 0 ? positional[0].ToLowerInvariant() : "help";
// `opm apply --json` writes one JSON object per line (progress, results, refusals) to stdout.
var jsonLines = json && command == "apply";
var output = new object();

try
{
    switch (command)
    {
        case "list":
            return await ListAsync();
        case "show" when positional.Length == 2 && int.TryParse(positional[1], out var diskNumber):
            return await ShowAsync(diskNumber);
        case "move" when positional.Length == 3 && int.TryParse(positional[1], out var moveDisk):
            return await MoveAsync(moveDisk, positional[2]);
        case "extend" when positional.Length == 3 && int.TryParse(positional[1], out var extendDisk):
            return await ExtendAsync(extendDisk, positional[2]);
        case "plan" when positional.Length == 3 && positional[1].Equals("new", StringComparison.OrdinalIgnoreCase):
            return PlanNew(positional[2]);
        case "plan" when positional.Length == 6 && positional[1].Equals("add", StringComparison.OrdinalIgnoreCase) && int.TryParse(positional[4], out var planDisk):
            return await PlanAddAsync(positional[2], positional[3], planDisk, positional[5]);
        case "plan" when positional.Length == 3 && positional[1].Equals("show", StringComparison.OrdinalIgnoreCase):
            return await PlanShowAsync(positional[2]);
        case "apply" when positional.Length == 2:
            return await ApplyAsync(positional[1]);
        case "resume":
            return await ResumeAsync(undo: false);
        case "undo":
            return await ResumeAsync(undo: true);
        case "version":
            Console.WriteLine($"{ProductInfo.Name} {ProductInfo.Version}");
            return 0;
        case "help" or "-h" or "/?":
            Help(Console.Out);
            return 0;
        default:
            Console.Error.WriteLine($"Unknown command: {string.Join(' ', args)}");
            Help(Console.Error);
            return 2;
    }
}
catch (Exception e) when (e is not OutOfMemoryException)
{
    Fail(e.Message);
    return 1;
}

void Fail(string message)
{
    if (jsonLines)
    {
        Emit(new { @event = "error", message });
    }
    else
    {
        Console.Error.WriteLine($"error: {message}");
    }
}

void Emit(object line)
{
    lock (output)
    {
        Console.WriteLine(JsonSerializer.Serialize(line, JsonOptions.Lines));
    }
}

async Task<int> ListAsync()
{
    var snapshot = await DiskDiscovery.CollectAsync();
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions.Default));
    }
    else
    {
        TextReport.Write(Console.Out, snapshot, detail: false);
    }
    return 0;
}

async Task<int> ShowAsync(int diskNumber)
{
    var snapshot = await DiskDiscovery.CollectAsync();
    var disk = snapshot.Disks.FirstOrDefault(d => d.Number == diskNumber);
    if (disk is null)
    {
        Console.Error.WriteLine($"error: disk {diskNumber} not found.");
        return 1;
    }
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(disk, JsonOptions.Default));
    }
    else
    {
        TextReport.Write(Console.Out, snapshot with { Disks = [disk] }, detail: true);
    }
    return 0;
}

async Task<(SystemSnapshot Snapshot, DiskInfo Disk, PartitionInfo Partition)> SelectAsync(int diskNumber, string which)
{
    var snapshot = await DiskDiscovery.CollectAsync();
    var disk = snapshot.Disks.FirstOrDefault(d => d.Number == diskNumber)
        ?? throw new PlanException($"Disk {diskNumber} not found.");
    return (snapshot, disk, Select(disk, which));
}

static PartitionInfo Select(DiskInfo disk, string which)
{
    var partition = which switch
    {
        _ when which.Equals("extended", StringComparison.OrdinalIgnoreCase) =>
            disk.Partitions.FirstOrDefault(p => p.Role == PartitionRole.Extended),
        ['@', .. var offset] when long.TryParse(offset, System.Globalization.CultureInfo.InvariantCulture, out var o) =>
            disk.Partitions.FirstOrDefault(p => p.Offset == o && p.Role != PartitionRole.Extended),
        [var letter, ..] when which.Length == 1 || (which.Length == 2 && which[1] == ':') =>
            disk.Partitions.FirstOrDefault(p => p.Volume?.DriveLetter is { } l && char.ToUpperInvariant(l) == char.ToUpperInvariant(letter)),
        _ => throw new PlanException($"'{which}' is not a drive letter (E:), 'extended', or @offset."),
    };
    return partition ?? throw new PlanException($"No partition '{which}' on disk {disk.Number}.");
}

async Task<int> MoveAsync(int diskNumber, string which)
{
    var (snapshot, disk, partition) = await SelectAsync(diskNumber, which);
    return await RunAsync(snapshot, MoveStepFor(disk, partition));
}

async Task<int> ExtendAsync(int diskNumber, string which)
{
    var (snapshot, disk, partition) = await SelectAsync(diskNumber, which);
    return await RunAsync(snapshot, ExtendStepFor(disk, partition));
}

MoveStep MoveStepFor(DiskInfo disk, PartitionInfo partition)
{
    var range = Planner.Range(disk, partition);
    var blockers = Planner.MoveBlockers(disk, partition);
    if (blockers.Count > 0 || range is null)
    {
        throw new PlanException($"{Planner.Name(partition)} can't be moved: {string.Join(" ", blockers)}");
    }
    var to = options.GetValueOrDefault("--to") switch
    {
        null or "" => throw new PlanException($"--to is required: start, end, or a byte offset from {range.Min} to {range.Max}."),
        "start" => range.Min,
        "end" => range.Max,
        var bytes => ParseBytes("--to", bytes),
    };
    return Planner.Move(disk, partition, to);
}

ExtendStep ExtendStepFor(DiskInfo disk, PartitionInfo partition)
{
    var blockers = Planner.ExtendBlockers(disk, partition);
    if (blockers.Count > 0)
    {
        throw new PlanException($"{Planner.Name(partition)} can't be extended: {string.Join(" ", blockers)}");
    }
    var size = options.GetValueOrDefault("--size") switch
    {
        null or "" or "max" => Planner.MaxSize(disk, partition),
        var bytes => ParseBytes("--size", bytes),
    };
    return Planner.Extend(disk, partition, size);
}

static long ParseBytes(string option, string text) =>
    long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bytes)
        ? bytes
        : throw new PlanException($"{option} {text}: give a number of bytes.");

int PlanNew(string path)
{
    if (File.Exists(path))
    {
        throw new PlanException($"{path} already exists. Nothing was changed. Pick another name or delete it first.");
    }
    SavePlan(path, PlanFile.New(DateTimeOffset.Now, Environment.MachineName));
    Console.WriteLine($"Created an empty plan in {path}. Add steps with 'opm plan add {path} move|extend ...'.");
    return 0;
}

async Task<int> PlanAddAsync(string path, string kind, int diskNumber, string which)
{
    var plan = LoadPlan(path);
    Func<DiskInfo, PlannedStep> make = kind.ToLowerInvariant() switch
    {
        "move" => projected => MoveStepFor(projected, Select(projected, which)),
        "extend" => projected => ExtendStepFor(projected, Select(projected, which)),
        _ => throw new PlanException($"'{kind}' is not a step: use move or extend."),
    };
    var snapshot = await DiskDiscovery.CollectAsync();
    plan = plan.Add(snapshot, diskNumber, make);
    SavePlan(path, plan);
    Console.WriteLine($"Saved {path}:");
    WriteSteps(plan.Steps);
    return 0;
}

async Task<int> PlanShowAsync(string path)
{
    var plan = LoadPlan(path);
    Console.WriteLine($"Plan {path}, made {plan.CreatedAt:yyyy-MM-dd HH:mm}{(plan.MachineName is null ? "" : $" on {plan.MachineName}")}:");
    WriteSteps(plan.Steps);
    if (plan.Steps.Count == 0)
    {
        return 0;
    }
    var snapshot = await DiskDiscovery.CollectAsync();
    var steps = plan.Validate(snapshot);
    foreach (var before in plan.Resolve(snapshot).Values)
    {
        var after = steps.Where(s => s.DiskNumber == before.Number).Aggregate(before, Planner.Apply);
        Console.WriteLine();
        Console.WriteLine("Now:");
        TextReport.WriteDisk(Console.Out, before, detail: false);
        Console.WriteLine();
        Console.WriteLine("After the plan:");
        TextReport.WriteDisk(Console.Out, after, detail: false);
        Console.WriteLine();
        Console.WriteLine(WriteGate.Explain(WriteGate.For(snapshot, before), before));
    }
    Console.WriteLine();
    Console.WriteLine($"Dry run: nothing was changed. Apply it with 'opm apply {path} --yes'.");
    return 0;
}

async Task<int> ApplyAsync(string path)
{
    var plan = LoadPlan(path);
    var snapshot = await DiskDiscovery.CollectAsync();
    var steps = plan.Validate(snapshot);
    for (var i = 0; i < steps.Count; i++)
    {
        if (jsonLines)
        {
            Emit(new { @event = "step", index = i + 1, total = steps.Count, disk = steps[i].DiskNumber, description = steps[i].Describe() });
        }
        else
        {
            Console.WriteLine($"  {i + 1}. {steps[i].Describe()}");
        }
    }
    var snapshotName = options.GetValueOrDefault("--snapshot");
    foreach (var disk in steps.Select(s => s.DiskNumber).Distinct().Select(n => snapshot.Disks.First(d => d.Number == n)))
    {
        var verdict = WriteGate.For(snapshot, disk);
        if (jsonLines)
        {
            Emit(new { @event = "gate", disk = disk.Number, verdict = verdict.ToString(), message = WriteGate.Explain(verdict, disk) });
        }
        else
        {
            Console.WriteLine(WriteGate.Explain(verdict, disk));
        }
        if (verdict == WriteVerdict.Refused)
        {
            Fail($"Disk {disk.Number} can't be changed by this version. Nothing was changed.");
            return 1;
        }
        if (verdict == WriteVerdict.NeedsSnapshot && string.IsNullOrWhiteSpace(snapshotName))
        {
            Fail($"Name the snapshot you took of disk {disk.Number} with --snapshot <name>. Nothing was changed.");
            return 1;
        }
    }
    if (!yes)
    {
        if (jsonLines)
        {
            Emit(new { @event = "dry-run", message = "Nothing was changed. Add --yes to do it." });
        }
        else
        {
            Console.WriteLine("Dry run: nothing was changed. Add --yes to do it.");
        }
        return 0;
    }
    var progress = new SyncProgress<(int Index, StepProgress Progress)>(p =>
    {
        if (jsonLines)
        {
            Emit(new { @event = "progress", index = p.Index + 1, total = steps.Count, fraction = p.Progress.Fraction, text = p.Progress.Text });
        }
        else
        {
            var prefix = $"  [{p.Index + 1}/{steps.Count}]";
            Console.WriteLine(p.Progress.Fraction is { } f ? $"{prefix} {f:P0}  {p.Progress.Text}" : $"{prefix} {p.Progress.Text}");
        }
    });
    var result = await Executor.RunAsync(snapshot, steps, snapshotName, progress);
    if (jsonLines)
    {
        Emit(new
        {
            @event = "result",
            succeeded = result.Succeeded,
            completed = result.Completed,
            total = result.Total,
            failedStep = result.FailedStep is null ? (int?)null : result.Completed + 1,
            error = result.Error,
        });
    }
    else if (result.Succeeded)
    {
        Console.WriteLine($"Done: all {result.Total} steps applied.");
    }
    else
    {
        Console.Error.WriteLine($"error: step {result.Completed + 1} ({result.FailedStep?.Describe()}) failed: {result.Error}");
        Console.Error.WriteLine(result.Completed == 0
            ? "No step was completed. Check the disk with 'opm show' (and 'opm resume' if a move was interrupted) before trying again."
            : $"Steps 1 to {result.Completed} are done and complete; the rest were not started. Check the disk with 'opm show' (and 'opm resume' if a move was interrupted), then make a new plan for what is left.");
    }
    return result.Succeeded ? 0 : 1;
}

static PlanFile LoadPlan(string path) =>
    File.Exists(path)
        ? PlanFile.FromJson(File.ReadAllText(path))
        : throw new PlanException($"Plan file {path} not found. Create one with 'opm plan new {path}'.");

static void SavePlan(string path, PlanFile plan)
{
    var temporary = path + ".tmp";
    File.WriteAllText(temporary, plan.ToJson() + Environment.NewLine);
    File.Move(temporary, path, overwrite: true);
}

static void WriteSteps(IReadOnlyList<PlannedStep> steps)
{
    if (steps.Count == 0)
    {
        Console.WriteLine("  (no steps yet)");
    }
    for (var i = 0; i < steps.Count; i++)
    {
        Console.WriteLine($"  {i + 1}. {steps[i].Describe()}");
    }
}

async Task<int> RunAsync(SystemSnapshot snapshot, PlannedStep step)
{
    var disk = snapshot.Disks.First(d => d.Number == step.DiskNumber);
    var verdict = WriteGate.For(snapshot, disk);
    Console.WriteLine($"Plan: {step.Describe()}");
    Console.WriteLine(WriteGate.Explain(verdict, disk));
    if (verdict == WriteVerdict.Refused)
    {
        return 1;
    }
    var snapshotName = options.GetValueOrDefault("--snapshot");
    if (verdict == WriteVerdict.NeedsSnapshot && string.IsNullOrWhiteSpace(snapshotName))
    {
        Console.Error.WriteLine("error: name the snapshot you took of this disk with --snapshot <name>.");
        return 1;
    }
    if (!yes)
    {
        Console.WriteLine("Dry run: nothing was changed. Add --yes to do it.");
        return 0;
    }
    var progress = new Progress<(int Index, StepProgress Progress)>(p =>
        Console.WriteLine(p.Progress.Fraction is { } f ? $"  {f:P0}  {p.Progress.Text}" : $"  {p.Progress.Text}"));
    var result = await Executor.RunAsync(snapshot, [step], snapshotName, progress);
    if (!result.Succeeded)
    {
        Console.Error.WriteLine($"error: {result.Error}");
        return 1;
    }
    Console.WriteLine("Done.");
    return 0;
}

async Task<int> ResumeAsync(bool undo)
{
    var pending = await Engine.FindPendingAsync();
    if (pending.Count == 0)
    {
        Console.WriteLine("No interrupted moves.");
        return 0;
    }
    var snapshot = await DiskDiscovery.CollectAsync();
    foreach (var move in pending)
    {
        var disk = snapshot.Disks.FirstOrDefault(d => Engine.DiskIdOf(d) == move.DiskId && d.Size == move.DiskSize);
        if (disk is null)
        {
            Console.Error.WriteLine($"error: the disk of interrupted move {Path.GetFileName(move.Journal)} is not attached.");
            return 1;
        }
        Console.WriteLine($"Interrupted move on disk {disk.Number}: {Units.FormatBytes(move.Length)} from {Units.FormatBytes(move.Source)} to {Units.FormatBytes(move.Target)} ({move.Chunks} chunks copied).");
        var verdict = WriteGate.For(snapshot, disk);
        var snapshotName = options.GetValueOrDefault("--snapshot");
        if (verdict == WriteVerdict.Refused || (verdict == WriteVerdict.NeedsSnapshot && string.IsNullOrWhiteSpace(snapshotName)))
        {
            Console.Error.WriteLine($"error: {WriteGate.Explain(verdict, disk)}");
            return 1;
        }
        if (undo && !move.CanUndo)
        {
            Console.Error.WriteLine("error: this move overlaps its original location, so it can't be undone; finish it with 'opm resume'.");
            return 1;
        }
        if (!yes)
        {
            Console.WriteLine(undo ? "Dry run: add --yes to put the partition back where it was." : "Dry run: add --yes to finish it.");
            continue;
        }
        var name = verdict == WriteVerdict.NeedsSnapshot ? snapshotName : null;
        var progress = new Progress<StepProgress>(p => Console.WriteLine(p.Fraction is { } f ? $"  {f:P0}  {p.Text}" : $"  {p.Text}"));
        if (undo)
        {
            await Engine.UndoAsync(move, disk.Number, name, progress);
            Console.WriteLine("Undone: the partition is back where it was.");
        }
        else
        {
            await Engine.RecoverAsync(move, disk.Number, name, progress);
            Console.WriteLine("Finished.");
        }
    }
    return 0;
}

static void Help(TextWriter output)
{
    output.WriteLine($"{ProductInfo.Name} {ProductInfo.Version} (preview)");
    output.WriteLine();
    output.WriteLine("Usage:");
    output.WriteLine("  opm list [--json]          Show every disk, its partitions and free space");
    output.WriteLine("  opm show <disk> [--json]   Show one disk in detail, with every warning and refusal");
    output.WriteLine("  opm move <disk> <which> --to start|end|<bytes> [--snapshot <name>] [--yes]");
    output.WriteLine("                             Move a partition within the free space around it");
    output.WriteLine("  opm extend <disk> <which> [--size max|<bytes>] [--snapshot <name>] [--yes]");
    output.WriteLine("                             Grow a volume into the free space after it");
    output.WriteLine("  opm plan new <file>        Start an empty plan: several steps previewed and applied together");
    output.WriteLine("  opm plan add <file> move <disk> <which> --to start|end|<bytes>");
    output.WriteLine("  opm plan add <file> extend <disk> <which> [--size max|<bytes>]");
    output.WriteLine("                             Add a step, checked against the disk as the earlier steps leave it");
    output.WriteLine("  opm plan show <file>       Dry run: the steps, and each disk now and after the plan");
    output.WriteLine("  opm apply <file> [--snapshot <name>] [--yes] [--json]");
    output.WriteLine("                             Check the plan against the disks again, then run it (--json: JSON lines)");
    output.WriteLine("  opm resume [--snapshot <name>] [--yes]");
    output.WriteLine("                             Finish a move that was interrupted");
    output.WriteLine("  opm undo [--snapshot <name>] [--yes]");
    output.WriteLine("                             Put an interrupted move's partition back where it was");
    output.WriteLine("  opm version");
    output.WriteLine();
    output.WriteLine("  <which> is a drive letter (E:), 'extended' (an MBR extended partition with its");
    output.WriteLine("  logical drives) or @<offset>. Without --yes nothing is changed (dry run).");
    output.WriteLine("  Real disks can only be changed on an Azure VM, after naming a snapshot (--snapshot).");
    output.WriteLine();
    output.WriteLine("Run from an elevated prompt for full details (BitLocker status needs Administrator).");
    output.WriteLine();
    output.WriteLine(ProductInfo.Disclaimer);
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One object per line, for <c>opm apply --json</c>.</summary>
    public static readonly JsonSerializerOptions Lines = new()
    {
        // Console output only, never embedded in HTML: keep quotes and apostrophes readable.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Reports on the caller's thread, so progress lines come out in order and before the result.</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
