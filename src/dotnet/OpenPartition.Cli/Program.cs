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
catch (PlanException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}
catch (Exception e) when (e is not OutOfMemoryException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
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
    return (snapshot, disk, partition ?? throw new PlanException($"No partition '{which}' on disk {diskNumber}."));
}

async Task<int> MoveAsync(int diskNumber, string which)
{
    var (snapshot, disk, partition) = await SelectAsync(diskNumber, which);
    var range = Planner.Range(disk, partition);
    var blockers = Planner.MoveBlockers(disk, partition);
    if (blockers.Count > 0 || range is null)
    {
        Console.Error.WriteLine($"error: {Planner.Name(partition)} can't be moved: {string.Join(" ", blockers)}");
        return 1;
    }
    var to = options.GetValueOrDefault("--to") switch
    {
        null or "" => throw new PlanException($"--to is required: start, end, or a byte offset from {range.Min} to {range.Max}."),
        "start" => range.Min,
        "end" => range.Max,
        var bytes => long.Parse(bytes, System.Globalization.CultureInfo.InvariantCulture),
    };
    return await RunAsync(snapshot, Planner.Move(disk, partition, to));
}

async Task<int> ExtendAsync(int diskNumber, string which)
{
    var (snapshot, disk, partition) = await SelectAsync(diskNumber, which);
    var blockers = Planner.ExtendBlockers(disk, partition);
    if (blockers.Count > 0)
    {
        Console.Error.WriteLine($"error: {Planner.Name(partition)} can't be extended: {string.Join(" ", blockers)}");
        return 1;
    }
    var size = options.GetValueOrDefault("--size") switch
    {
        null or "" or "max" => Planner.MaxSize(disk, partition),
        var bytes => long.Parse(bytes, System.Globalization.CultureInfo.InvariantCulture),
    };
    return await RunAsync(snapshot, Planner.Extend(disk, partition, size));
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
}
