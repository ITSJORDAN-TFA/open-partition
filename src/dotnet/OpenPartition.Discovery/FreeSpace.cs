using OpenPartition.Model;

namespace OpenPartition.Discovery;

public static class FreeSpace
{
    /// <summary>Partitions are placed on 1 MiB boundaries; smaller gaps are not usable.</summary>
    public const long Alignment = Units.MiB;

    /// <summary>
    /// Gaps between partitions within [usableStart, usableEnd), trimmed inwards to 1 MiB
    /// boundaries and dropped if smaller than 1 MiB. Logical partitions are ignored: the
    /// extended partition that contains them counts as occupied.
    /// </summary>
    public static IReadOnlyList<FreeExtent> Compute(long usableStart, long usableEnd, IEnumerable<PartitionInfo> partitions)
    {
        var occupied = partitions.Where(p => !p.IsLogical && p.Size > 0).OrderBy(p => p.Offset);
        var result = new List<FreeExtent>();
        var cursor = usableStart;
        foreach (var p in occupied)
        {
            Add(result, cursor, Math.Min(p.Offset, usableEnd));
            cursor = Math.Max(cursor, p.End);
        }
        Add(result, cursor, usableEnd);
        return result;
    }

    private static void Add(List<FreeExtent> result, long start, long end)
    {
        var alignedStart = (start + Alignment - 1) / Alignment * Alignment;
        var alignedEnd = end / Alignment * Alignment;
        if (alignedEnd - alignedStart >= Alignment)
        {
            result.Add(new FreeExtent(alignedStart, alignedEnd - alignedStart));
        }
    }
}
