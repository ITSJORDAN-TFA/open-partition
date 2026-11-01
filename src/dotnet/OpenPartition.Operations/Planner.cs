using OpenPartition.Discovery;
using OpenPartition.Model;

namespace OpenPartition.Operations;

/// <summary>A change to one disk, applied in queue order.</summary>
public abstract record PlannedStep(int DiskNumber, string What)
{
    public abstract string Describe();
}

/// <summary>Move a partition (or an extended partition with its logical drives) to <paramref name="To"/>.</summary>
public sealed record MoveStep(int DiskNumber, string What, long From, long To, long Length) : PlannedStep(DiskNumber, What)
{
    public override string Describe() =>
        $"Move {What} on disk {DiskNumber} {(To > From ? "right" : "left")} by {Units.FormatBytes(Math.Abs(To - From))} (to {Units.FormatBytes(To)})";
}

/// <summary>
/// Grow a partition and its file system into the free space after it, with Windows' own resize.
/// For an MBR extended partition (<paramref name="Container"/>) only the container grows, so its
/// last logical drive can then be extended into the new room.
/// </summary>
public sealed record ExtendStep(int DiskNumber, string What, long Offset, long OldSize, long NewSize, bool Container = false) : PlannedStep(DiskNumber, What)
{
    public override string Describe() => Container
        ? $"Grow {What} on disk {DiskNumber} from {Units.FormatBytes(OldSize)} to {Units.FormatBytes(NewSize)} (room for its last logical drive)"
        : $"Extend {What} on disk {DiskNumber} from {Units.FormatBytes(OldSize)} to {Units.FormatBytes(NewSize)}";
}

/// <summary>Byte range a partition may start in, both ends 1 MiB aligned and inclusive.</summary>
public sealed record MoveRange(long Min, long Max);

/// <summary>
/// Checks queued operations against a disk layout and works out the layout they produce, so
/// several operations can be planned and previewed before anything touches the disk. Pure:
/// nothing here reads or writes a disk.
/// </summary>
public static class Planner
{
    public const long Alignment = FreeSpace.Alignment;

    /// <summary>Partitions that move with <paramref name="p"/>: itself and, for an extended partition, its logical drives.</summary>
    public static IReadOnlyList<PartitionInfo> MovingWith(DiskInfo disk, PartitionInfo p) =>
        p.Role == PartitionRole.Extended
            ? [p, .. disk.Partitions.Where(c => c.IsLogical && c.Offset >= p.Offset && c.End <= p.End)]
            : [p];

    /// <summary>Why <paramref name="p"/> can't be moved; empty when it can.</summary>
    public static IReadOnlyList<string> MoveBlockers(DiskInfo disk, PartitionInfo p)
    {
        var reasons = disk.BlockersFor(p, OperationKind.Move).Select(i => i.Message).ToList();
        if (p.Role == PartitionRole.Extended)
        {
            // Each logical drive comes along, so its own reasons count (except "logical drive").
            foreach (var child in MovingWith(disk, p).Skip(1))
            {
                var name = Name(child);
                reasons.AddRange(child.Issues
                    .Where(i => i.BlocksOperation(OperationKind.Move) && i.Code != "partition.logical")
                    .Select(i => $"{name}: {i.Message}"));
            }
        }
        if (reasons.Count == 0 && Range(disk, p) is null)
        {
            reasons.Add("There is no free space next to it to move into.");
        }
        return reasons;
    }

    /// <summary>Where <paramref name="p"/> can start: anywhere in the free space around it.</summary>
    public static MoveRange? Range(DiskInfo disk, PartitionInfo p)
    {
        var gap = p.IsLogical ? LogicalMoveGap(disk, p) : Gap(disk, p);
        if (gap is not { } found)
        {
            return null;
        }
        var (gapStart, gapEnd) = found;
        var min = AlignUp(gapStart);
        var max = AlignDown(gapEnd - p.Size);
        if (min > max || (min == max && min == p.Offset))
        {
            return null;
        }
        return new MoveRange(min, max);
    }

    /// <summary>Why <paramref name="p"/> can't be extended; empty when it can.</summary>
    public static IReadOnlyList<string> ExtendBlockers(DiskInfo disk, PartitionInfo p)
    {
        var reasons = disk.BlockersFor(p, OperationKind.Extend).Select(i => i.Message).ToList();
        if (reasons.Count == 0 && p.Role != PartitionRole.Extended && p.Volume?.FileSystem is not ("NTFS" or "ReFS"))
        {
            reasons.Add("Only NTFS and ReFS volumes can be extended.");
        }
        if (reasons.Count == 0 && MaxSize(disk, p) <= p.Size)
        {
            var container = ContainerOf(disk, p);
            reasons.Add(container is not null && container.End == p.End && MaxSize(disk, container) > container.Size
                ? "There is no free space left inside the extended partition. Extend the extended partition first, then this drive."
                : "There is no free space directly after it.");
        }
        return reasons;
    }

    /// <summary>Largest size <paramref name="p"/> can grow to: up to the next partition or the end of the usable area.</summary>
    public static long MaxSize(DiskInfo disk, PartitionInfo p)
    {
        var (_, gapEnd) = Gap(disk, p);
        return Math.Max(p.Size, AlignDown(gapEnd) - p.Offset);
    }

    public static MoveStep Move(DiskInfo disk, PartitionInfo p, long to)
    {
        var blockers = MoveBlockers(disk, p);
        if (blockers.Count > 0)
        {
            throw new PlanException(blockers[0]);
        }
        var range = Range(disk, p)!;
        if (to % Alignment != 0 || to < range.Min || to > range.Max || to == p.Offset)
        {
            throw new PlanException($"{Name(p)} can start anywhere from {Units.FormatBytes(range.Min)} to {Units.FormatBytes(range.Max)} on a 1 MiB boundary, other than where it is now.");
        }
        return new MoveStep(disk.Number, Name(p), p.Offset, to, p.Size);
    }

    public static ExtendStep Extend(DiskInfo disk, PartitionInfo p, long newSize)
    {
        var blockers = ExtendBlockers(disk, p);
        if (blockers.Count > 0)
        {
            throw new PlanException(blockers[0]);
        }
        var max = MaxSize(disk, p);
        if (newSize <= p.Size || newSize > max)
        {
            throw new PlanException($"{Name(p)} can grow to at most {Units.FormatBytes(max)}.");
        }
        return new ExtendStep(disk.Number, Name(p), p.Offset, p.Size, newSize, p.Role == PartitionRole.Extended);
    }

    /// <summary>
    /// Add <paramref name="step"/> to the end of <paramref name="queue"/>, folding it into the
    /// last step when that changes the same partition: two moves in a row become one move from
    /// the first start to the last target (or disappear if it ends where it began), and two
    /// extends become one. Each move copies the whole partition, so this can save hours. A
    /// merged move is re-checked against the layout before it; if it isn't valid on its own,
    /// both steps are kept.
    /// </summary>
    public static IReadOnlyList<PlannedStep> Append(DiskInfo original, IReadOnlyList<PlannedStep> queue, PlannedStep step)
    {
        if (queue.Count == 0 || queue[^1].DiskNumber != step.DiskNumber || original.Number != step.DiskNumber)
        {
            return [.. queue, step];
        }
        var rest = queue.Take(queue.Count - 1).ToList();
        switch (queue[^1], step)
        {
            case (MoveStep last, MoveStep next) when last.To == next.From:
                if (next.To == last.From)
                {
                    return rest;
                }
                var before = rest.Where(s => s.DiskNumber == original.Number).Aggregate(original, Apply);
                var partition = At(before, last.From);
                if (partition is not null)
                {
                    try
                    {
                        return [.. rest, Move(before, partition, next.To)];
                    }
                    catch (PlanException)
                    {
                        // Not valid as a single move; keep both.
                    }
                }
                break;
            case (ExtendStep last, ExtendStep next) when last.Offset == next.Offset && last.Container == next.Container:
                return [.. rest, next with { OldSize = last.OldSize }];
        }
        return [.. queue, step];
    }

    /// <summary>The disk as it will be once <paramref name="step"/> has run.</summary>
    public static DiskInfo Apply(DiskInfo disk, PlannedStep step)
    {
        IEnumerable<PartitionInfo> partitions = step switch
        {
            MoveStep m => Shift(disk, Find(disk, m.From), m.To - m.From),
            ExtendStep e => disk.Partitions.Select(p => p.Offset == e.Offset && (p.Role == PartitionRole.Extended) == e.Container
                ? p with { Size = e.NewSize, Volume = Grow(p.Volume, e.NewSize - e.OldSize) }
                : p),
            _ => throw new ArgumentException($"Unknown step {step.GetType().Name}.", nameof(step)),
        };
        var list = partitions.OrderBy(p => p.Offset).ToList();
        return disk with
        {
            Partitions = list,
            FreeExtents = FreeSpace.Compute(disk.UsableStart, disk.UsableEnd, list),
        };
    }

    public static string Name(PartitionInfo p) => p switch
    {
        { Role: PartitionRole.Extended } => "the extended partition",
        { Volume.DriveLetter: { } letter } => $"{letter}:",
        { Volume.Label: { Length: > 0 } label } => $"'{label}'",
        _ => $"partition {p.Number} at {Units.FormatBytes(p.Offset)}",
    };

    private static PartitionInfo Find(DiskInfo disk, long offset) =>
        At(disk, offset)
        ?? throw new PlanException($"No partition starts at byte {offset} on disk {disk.Number}.");

    /// <summary>The partition starting at <paramref name="offset"/>: a primary or extended one, else a logical drive.</summary>
    private static PartitionInfo? At(DiskInfo disk, long offset) =>
        disk.Partitions.FirstOrDefault(p => p.Offset == offset && !p.IsLogical)
        ?? disk.Partitions.FirstOrDefault(p => p.Offset == offset && p.IsLogical);

    private static IEnumerable<PartitionInfo> Shift(DiskInfo disk, PartitionInfo moving, long delta)
    {
        var group = MovingWith(disk, moving).ToHashSet();
        // A logical drive's EBR moves with it, apart from the first one's, at the container
        // start, which only moves with the whole extended partition.
        var firstEbr = ContainerOf(disk, moving)?.Offset;
        return disk.Partitions.Select(p => group.Contains(p)
            ? p with
            {
                Offset = p.Offset + delta,
                EbrOffset = p.EbrOffset is { } ebr && ebr != firstEbr ? ebr + delta : p.EbrOffset,
            }
            : p);
    }

    private static VolumeInfo? Grow(VolumeInfo? volume, long delta) => volume switch
    {
        null => null,
        _ => volume with
        {
            Capacity = volume.Capacity + delta,
            FreeSpace = volume.FreeSpace + delta,
        },
    };

    /// <summary>The extended partition holding logical drive <paramref name="p"/>, if it is one.</summary>
    private static PartitionInfo? ContainerOf(DiskInfo disk, PartitionInfo p) =>
        p.IsLogical ? disk.Partitions.FirstOrDefault(c => c.Role == PartitionRole.Extended && c.Offset <= p.Offset && p.End <= c.End) : null;

    /// <summary>
    /// The free space around <paramref name="p"/>, between its neighbours at the same level. A
    /// logical drive is also bounded by its extended partition.
    /// </summary>
    private static (long Start, long End) Gap(DiskInfo disk, PartitionInfo p)
    {
        var siblings = disk.Partitions.Where(o => o != p && o.IsLogical == p.IsLogical && o.Size > 0).ToList();
        var container = ContainerOf(disk, p);
        var floor = container?.Offset ?? disk.UsableStart;
        var ceiling = container?.End ?? disk.UsableEnd;
        var start = siblings.Where(o => o.End <= p.Offset).Select(o => o.End).Append(floor).Max();
        var end = siblings.Where(o => o.Offset >= p.End).Select(o => o.Offset).Append(ceiling).Min();
        if (container is not null && end < ceiling)
        {
            // The gap before the next logical drive holds that drive's EBR (the link in the
            // chain), so it is never free space: only the last logical drive can grow.
            end = p.End;
        }
        return (start, end);
    }

    /// <summary>
    /// Where a logical drive's data may lie when it moves: inside its extended partition, after
    /// the drive before it and before the next drive's EBR. Its own EBR moves with it and keeps
    /// its distance in front of the data, except the first drive's, which stays at the start of
    /// the extended partition. <c>null</c> when the chain isn't known well enough.
    /// </summary>
    private static (long Start, long End)? LogicalMoveGap(DiskInfo disk, PartitionInfo p)
    {
        if (ContainerOf(disk, p) is not { } container || p.EbrOffset is not { } ebr || ebr >= p.Offset || ebr < container.Offset)
        {
            return null;
        }
        var sector = disk.LogicalSectorSize;
        var others = disk.Partitions.Where(o => o != p && o.IsLogical && o.Size > 0).ToList();
        if (others.Any(o => o.EbrOffset is null))
        {
            return null;
        }
        // The first drive's EBR is the one at the container start; nothing may go over it.
        var lead = ebr == container.Offset ? 0 : p.Offset - ebr;
        var floor = others.Where(o => o.End <= p.Offset).Select(o => o.End).Append(container.Offset + sector).Max();
        var end = others.Where(o => o.Offset >= p.End).Select(o => o.EbrOffset!.Value).Append(container.End).Min();
        return (floor + lead, end);
    }

    private static long AlignUp(long v) => (v + Alignment - 1) / Alignment * Alignment;

    private static long AlignDown(long v) => v / Alignment * Alignment;
}

public sealed class PlanException(string message) : Exception(message);
