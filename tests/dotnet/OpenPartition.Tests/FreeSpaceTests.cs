using OpenPartition.Discovery;
using OpenPartition.Model;

namespace OpenPartition.Tests;

public class FreeSpaceTests
{
    private const long M = Units.MiB;

    private static PartitionInfo P(long offsetMiB, long sizeMiB, bool logical = false) => new()
    {
        Number = 0,
        Offset = offsetMiB * M,
        Size = sizeMiB * M,
        Role = PartitionRole.Data,
        IsLogical = logical,
    };

    [Fact]
    public void FindsGapsBetweenAndAroundPartitions()
    {
        var free = FreeSpace.Compute(17408, 100 * M, [P(10, 20), P(50, 10)]);
        Assert.Equal(
            [new FreeExtent(1 * M, 9 * M), new FreeExtent(30 * M, 20 * M), new FreeExtent(60 * M, 40 * M)],
            free);
    }

    [Fact]
    public void TrimsToMebibyteBoundariesAndDropsSmallGaps()
    {
        // GPT usable area ends 16.5 KiB before the disk end; a 0.5 MiB gap is not usable.
        var end = (100 * M) - 16896;
        var free = FreeSpace.Compute(17408, end, [P(1, 49), P(50, 49)]);
        Assert.Empty(free);
    }

    [Fact]
    public void LogicalPartitionsDoNotCreateGapsInsideTheExtendedContainer()
    {
        var free = FreeSpace.Compute(512, 100 * M, [P(1, 49), P(2, 10, logical: true)]);
        Assert.Equal([new FreeExtent(50 * M, 50 * M)], free);
    }

    [Fact]
    public void EmptyDiskIsOneExtent()
    {
        Assert.Equal([new FreeExtent(1 * M, 99 * M)], FreeSpace.Compute(17408, 100 * M, []));
    }
}
