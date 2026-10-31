using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenPartition.Model;

namespace OpenPartition.Operations;

/// <summary>
/// A disk as it was when the first step for it went into a plan: its identity (the same ID the
/// engine's journal uses), its size and a fingerprint of its partition layout. A plan only
/// applies to a disk that still matches all three.
/// </summary>
public sealed record PlanDisk(int Number, string DiskId, long Size, string Fingerprint);

/// <summary>
/// A queue of planned steps saved to a file, so it can be built with several <c>opm plan add</c>
/// calls, previewed and applied later (the CLI's version of the GUI's planned-changes queue).
/// Steps are kept in the order they run and merged with <see cref="Planner.Append"/>, exactly
/// like the GUI. Pure: nothing here reads or writes a disk.
/// </summary>
public sealed record PlanFile(DateTimeOffset CreatedAt, string? MachineName, IReadOnlyList<PlanDisk> Disks, IReadOnlyList<PlannedStep> Steps)
{
    /// <summary>Plan file format. Bump it when the JSON changes in a way older versions can't read.</summary>
    public const int FormatVersion = 1;

    public static PlanFile New(DateTimeOffset createdAt, string? machineName) => new(createdAt, machineName, [], []);

    /// <summary>
    /// Fingerprint of a disk's partition layout: geometry plus every partition's place, size and
    /// type. Volume contents (free space, BitLocker, page files) are not part of it; those are
    /// re-checked step by step when the plan is applied.
    /// </summary>
    public static string Fingerprint(DiskInfo disk)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{disk.Style}|{disk.Size}|{disk.LogicalSectorSize}|{disk.UsableStart}|{disk.UsableEnd}");
        foreach (var p in disk.Partitions.OrderBy(p => p.Offset).ThenBy(p => p.IsLogical))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{p.Offset}|{p.Size}|{p.Role}|{p.IsLogical}|{p.MbrType}|{p.GptType}|{p.PartitionGuid}");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static PlanDisk Identify(DiskInfo disk) => new(disk.Number, Engine.DiskIdOf(disk), disk.Size, Fingerprint(disk));

    /// <summary>
    /// The live disk each planned disk refers to, keyed by the disk number in the plan. Disk
    /// numbers can change after a reboot, so disks are found by ID and size. Refuses when a disk
    /// is missing or its layout changed since the plan was made: the plan was worked out for the
    /// old layout and can't be trusted on the new one.
    /// </summary>
    public IReadOnlyDictionary<int, DiskInfo> Resolve(SystemSnapshot live)
    {
        var map = new Dictionary<int, DiskInfo>();
        foreach (var planned in Disks)
        {
            var disk = live.Disks.FirstOrDefault(d => Engine.DiskIdOf(d) == planned.DiskId && d.Size == planned.Size)
                ?? throw new PlanException($"Disk {planned.Number} of this plan (ID {planned.DiskId}, {Units.FormatBytes(planned.Size)}) is not attached. Nothing was changed.");
            if (Fingerprint(disk) != planned.Fingerprint)
            {
                throw new PlanException($"The partitions on disk {disk.Number} changed since this plan was made, so it no longer fits. Nothing was changed. Make a new plan with 'opm plan new'.");
            }
            map[planned.Number] = disk;
        }
        return map;
    }

    /// <summary>
    /// Add a step for a disk of <paramref name="live"/>. <paramref name="make"/> builds it against
    /// the disk as projected after the plan's earlier steps (and throws <see cref="PlanException"/>
    /// when it isn't possible there); it is then merged into the queue like the GUI does.
    /// </summary>
    public PlanFile Add(SystemSnapshot live, int diskNumber, Func<DiskInfo, PlannedStep> make)
    {
        var resolved = Resolve(live);
        if (resolved.Any(r => r.Key != r.Value.Number))
        {
            throw new PlanException("Disk numbers changed since this plan was started (Windows renumbers disks after a reboot or when disks are attached). Nothing was added. Make a new plan with 'opm plan new'.");
        }
        var disk = live.Disks.FirstOrDefault(d => d.Number == diskNumber)
            ?? throw new PlanException($"Disk {diskNumber} not found.");
        var disks = resolved.ContainsKey(diskNumber) ? Disks : [.. Disks, Identify(disk)];
        var step = make(Project(disk));
        return this with { Disks = disks, Steps = Planner.Append(disk, Steps, step) };
    }

    /// <summary><paramref name="disk"/> as it will be once this plan's steps for it have run.</summary>
    public DiskInfo Project(DiskInfo disk) =>
        Steps.Where(s => s.DiskNumber == disk.Number).Aggregate(disk, Planner.Apply);

    /// <summary>
    /// Checks every step again, in order, against the live disks (each against the layout the
    /// steps before it produce) and returns the steps to run, renumbered to the disks' current
    /// numbers. Refuses if any disk changed or any step is no longer possible.
    /// </summary>
    public IReadOnlyList<PlannedStep> Validate(SystemSnapshot live)
    {
        if (Steps.Count == 0)
        {
            throw new PlanException("This plan has no steps. Add some with 'opm plan add'.");
        }
        var resolved = Resolve(live);
        var projected = resolved.ToDictionary(r => r.Key, r => r.Value);
        var steps = new List<PlannedStep>();
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (!projected.TryGetValue(step.DiskNumber, out var disk))
            {
                throw new PlanException($"Step {i + 1} is for disk {step.DiskNumber}, which this plan has no record of. The plan file is damaged; make a new one.");
            }
            PlannedStep check;
            try
            {
                check = Recheck(disk, step);
            }
            catch (PlanException e)
            {
                throw new PlanException($"Step {i + 1} ({step.Describe()}) is no longer possible: {e.Message} Nothing was changed.");
            }
            steps.Add(check);
            projected[step.DiskNumber] = Planner.Apply(disk, check);
        }
        return steps;
    }

    private static PlannedStep Recheck(DiskInfo disk, PlannedStep step)
    {
        switch (step)
        {
            case MoveStep move:
                var moving = disk.Partitions.FirstOrDefault(p => p.Offset == move.From && !p.IsLogical);
                if (moving is null || moving.Size != move.Length)
                {
                    throw new PlanException($"No partition of {Units.FormatBytes(move.Length)} starts at byte {move.From}.");
                }
                return Planner.Move(disk, moving, move.To);
            case ExtendStep extend:
                var growing = disk.Partitions.FirstOrDefault(p => p.Offset == extend.Offset && (p.Role == PartitionRole.Extended) == extend.Container);
                if (growing is null || growing.Size != extend.OldSize)
                {
                    throw new PlanException($"No partition of {Units.FormatBytes(extend.OldSize)} starts at byte {extend.Offset}.");
                }
                return Planner.Extend(disk, growing, extend.NewSize);
            default:
                throw new PlanException($"Unknown step {step.GetType().Name}.");
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(new FileDto
    {
        Format = FormatVersion,
        CreatedBy = $"{ProductInfo.Name} {ProductInfo.Version}",
        CreatedAt = CreatedAt,
        Machine = MachineName,
        Disks = [.. Disks],
        Steps = [.. Steps.Select(StepDto.Of)],
    }, Json);

    public static PlanFile FromJson(string json)
    {
        FileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<FileDto>(json, Json);
        }
        catch (JsonException e)
        {
            throw new PlanException($"This is not a plan file ({e.Message}).");
        }
        if (dto is null || dto.Format is null)
        {
            throw new PlanException("This is not a plan file (no format version).");
        }
        if (dto.Format != FormatVersion)
        {
            throw new PlanException($"This plan file has format {dto.Format}; this version of {ProductInfo.Name} reads format {FormatVersion}. Make a new plan with this version.");
        }
        var disks = dto.Disks ?? [];
        if (disks.Any(d => string.IsNullOrEmpty(d.DiskId) || string.IsNullOrEmpty(d.Fingerprint)) || disks.DistinctBy(d => d.Number).Count() != disks.Count)
        {
            throw new PlanException("The plan file is damaged (a disk entry is missing its ID or fingerprint, or is listed twice). Make a new plan.");
        }
        var steps = (dto.Steps ?? []).Select(s => s.ToStep()).ToList();
        if (steps.Any(s => disks.All(d => d.Number != s.DiskNumber)))
        {
            throw new PlanException("The plan file is damaged (a step is for a disk the plan has no record of). Make a new plan.");
        }
        return new PlanFile(dto.CreatedAt ?? DateTimeOffset.MinValue, dto.Machine, disks, steps);
    }

    private sealed class FileDto
    {
        public int? Format { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public string? Machine { get; set; }
        public List<PlanDisk>? Disks { get; set; }
        public List<StepDto>? Steps { get; set; }
    }

    private sealed class StepDto
    {
        public string? Kind { get; set; }
        public int Disk { get; set; }
        public string? What { get; set; }
        public long? From { get; set; }
        public long? To { get; set; }
        public long? Length { get; set; }
        public long? Offset { get; set; }
        public long? OldSize { get; set; }
        public long? NewSize { get; set; }
        public bool? Container { get; set; }

        public static StepDto Of(PlannedStep step) => step switch
        {
            MoveStep m => new StepDto { Kind = "move", Disk = m.DiskNumber, What = m.What, From = m.From, To = m.To, Length = m.Length },
            ExtendStep e => new StepDto { Kind = "extend", Disk = e.DiskNumber, What = e.What, Offset = e.Offset, OldSize = e.OldSize, NewSize = e.NewSize, Container = e.Container },
            _ => throw new ArgumentException($"Unknown step {step.GetType().Name}.", nameof(step)),
        };

        public PlannedStep ToStep() => (Kind, From, To, Length, Offset, OldSize, NewSize) switch
        {
            ("move", { } from, { } to, { } length, _, _, _) => new MoveStep(Disk, What ?? "", from, to, length),
            ("extend", _, _, _, { } offset, { } oldSize, { } newSize) => new ExtendStep(Disk, What ?? "", offset, oldSize, newSize, Container ?? false),
            _ => throw new PlanException($"The plan file is damaged (a '{Kind}' step is missing fields). Make a new plan."),
        };
    }
}
