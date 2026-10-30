using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPartition.Cli;
using OpenPartition.Discovery;
using OpenPartition.Model;

var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
var command = positional.Length > 0 ? positional[0].ToLowerInvariant() : "help";

try
{
    switch (command)
    {
        case "list":
            return await ListAsync();
        case "show" when positional.Length == 2 && int.TryParse(positional[1], out var diskNumber):
            return await ShowAsync(diskNumber);
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

static void Help(TextWriter output)
{
    output.WriteLine($"{ProductInfo.Name} {ProductInfo.Version} (read-only preview)");
    output.WriteLine();
    output.WriteLine("Usage:");
    output.WriteLine("  opm list [--json]          Show every disk, its partitions and free space");
    output.WriteLine("  opm show <disk> [--json]   Show one disk in detail, with every warning and refusal");
    output.WriteLine("  opm version");
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
