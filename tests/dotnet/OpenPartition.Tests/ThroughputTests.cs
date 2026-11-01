using OpenPartition.Model;
using OpenPartition.Operations;

namespace OpenPartition.Tests;

/// <summary>The move time estimate before Apply, and the live speed and time left during a move.</summary>
public class ThroughputTests
{
    private const long MiB = Units.MiB;
    private const long GiB = 1024 * MiB;

    private static SystemSnapshot Snapshot(params PartitionInfo[] partitions) => new()
    {
        CollectedAt = DateTimeOffset.UtcNow,
        MachineName = "test",
        OsVersion = "test",
        IsElevated = false,
        Disks =
        [
            new DiskInfo
            {
                Number = 1,
                Size = 100 * GiB,
                LogicalSectorSize = 512,
                PhysicalSectorSize = 512,
                Style = PartitionStyle.Gpt,
                BusType = "SAS",
                Partitions = partitions,
            },
        ],
    };

    private static PartitionInfo Part(long offset, long size, string? fileSystem, long? free, bool logical = false, PartitionRole role = PartitionRole.Data) => new()
    {
        Number = (int)(offset / GiB) + 1,
        Offset = offset,
        Size = size,
        Role = role,
        IsLogical = logical,
        Volume = fileSystem is null ? null : new VolumeInfo
        {
            Path = $@"\\?\Volume{{{offset:D8}-0000-0000-0000-000000000000}}\",
            FileSystem = fileSystem,
            BitLocker = BitLockerState.NotEncrypted,
            Capacity = size,
            FreeSpace = free,
        },
    };

    [Fact]
    public void FreeSpaceIsConservativeUntilALockedBitmapIsAvailable()
    {
        var snapshot = Snapshot(Part(GiB, 10 * GiB, "NTFS", 6 * GiB));
        var step = new MoveStep(1, "D:", GiB, 20 * GiB, 10 * GiB);
        Assert.Equal(10 * GiB, Throughput.BytesToCopy(snapshot, step));
    }

    [Fact]
    public void UnknownFreeSpaceOrOtherFileSystemsCopyEverything()
    {
        Assert.Equal(10 * GiB, Throughput.BytesToCopy(Snapshot(Part(GiB, 10 * GiB, "NTFS", null)), new MoveStep(1, "D:", GiB, 20 * GiB, 10 * GiB)));
        Assert.Equal(10 * GiB, Throughput.BytesToCopy(Snapshot(Part(GiB, 10 * GiB, "FAT32", 6 * GiB)), new MoveStep(1, "D:", GiB, 20 * GiB, 10 * GiB)));
        Assert.Equal(10 * GiB, Throughput.BytesToCopy(Snapshot(), new MoveStep(1, "D:", GiB, 20 * GiB, 10 * GiB)));
    }

    [Fact]
    public void AnExtendedPartitionEstimateAllowsForFullCopyFallback()
    {
        var snapshot = Snapshot(
            Part(GiB, 10 * GiB, null, null, role: PartitionRole.Extended),
            Part(GiB + MiB, 4 * GiB, "NTFS", 3 * GiB, logical: true),
            Part(6 * GiB, 4 * GiB, "NTFS", GiB, logical: true));
        Assert.Equal(10 * GiB, Throughput.BytesToCopy(snapshot, new MoveStep(1, "Extended", GiB, 20 * GiB, 10 * GiB)));
    }

    [Fact]
    public void AMoveTransfersEveryCopiedByteFourTimes()
    {
        // 1 GiB at 100 MiB/s: fingerprint 10 s, copy (read + write) 20 s, check 10 s.
        var estimate = Throughput.EstimateMove(GiB, 100 * MiB);
        Assert.Equal(Throughput.MoveOverhead + TimeSpan.FromSeconds(40.96), estimate);
        Assert.Throws<ArgumentOutOfRangeException>(() => Throughput.EstimateMove(GiB, 0));
    }

    [Theory]
    [InlineData(30, "under a minute")]
    [InlineData(70, "about a minute")]
    [InlineData(4 * 60 + 10, "about 4 minutes")]
    [InlineData(80 * 60, "about 1 h 20 min")]
    public void DurationsReadNaturally(int seconds, string expected) =>
        Assert.Equal(expected, Throughput.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TheFirstProgressLineHasNoRateYet()
    {
        var meter = new ThroughputMeter();
        Assert.Equal((null, null), meter.Sample("copying", 128 * MiB, GiB, TimeSpan.FromSeconds(1)));
        Assert.Equal("", Throughput.Describe(null, null));
    }

    [Fact]
    public void CopyingRateIncludesVerificationAlreadyDoneInEachChunk()
    {
        var meter = new ThroughputMeter();
        meter.Sample("copying", 0, 1000 * MiB, TimeSpan.Zero);
        var (rate, left) = meter.Sample("copying", 200 * MiB, 1000 * MiB, TimeSpan.FromSeconds(2));
        Assert.Equal(100.0 * MiB, rate!.Value, 3);
        // 800 MiB left at 100 MiB/s; each chunk has already been read back.
        Assert.Equal(8, left!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void HashingTimeLeftIncludesVerifiedCopying()
    {
        var meter = new ThroughputMeter();
        meter.Sample("hashing", 0, 1000 * MiB, TimeSpan.Zero);
        var (_, left) = meter.Sample("hashing", 500 * MiB, 1000 * MiB, TimeSpan.FromSeconds(1));
        // 1 s of hashing left, then 6 s to read, write and verify the copy.
        Assert.Equal(7, left!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void ANewStageStartsANewRateAndTheRateFollowsRecentSpeed()
    {
        var meter = new ThroughputMeter { Window = TimeSpan.FromSeconds(10) };
        meter.Sample("hashing", 0, 1000 * MiB, TimeSpan.Zero);
        meter.Sample("hashing", 1000 * MiB, 1000 * MiB, TimeSpan.FromSeconds(1));
        Assert.Equal((null, null), meter.Sample("copying", 10 * MiB, 1000 * MiB, TimeSpan.FromSeconds(2)));

        // Fast at first, then the disk slows to 10 MiB/s; after the window only the slow part counts.
        for (var s = 3; s <= 30; s++)
        {
            var done = s <= 5 ? 10 * MiB + (s - 2) * 100 * MiB : 310 * MiB + (s - 5) * 10 * MiB;
            var (rate, _) = meter.Sample("copying", done, 1000 * MiB, TimeSpan.FromSeconds(s));
            if (s == 30)
            {
                Assert.Equal(10.0 * MiB, rate!.Value, 3);
            }
        }
    }

    [Fact]
    public void SlowChunksStillGiveARate()
    {
        // One chunk every 15 s, longer than the window: the previous sample is kept.
        var meter = new ThroughputMeter { Window = TimeSpan.FromSeconds(10) };
        meter.Sample("copying", 0, 1000 * MiB, TimeSpan.Zero);
        meter.Sample("copying", 128 * MiB, 1000 * MiB, TimeSpan.FromSeconds(15));
        var (rate, _) = meter.Sample("copying", 256 * MiB, 1000 * MiB, TimeSpan.FromSeconds(30));
        Assert.Equal(128.0 * MiB / 15, rate!.Value, 3);
    }

    [Fact]
    public void ProgressTextShowsSpeedAndTimeLeft() =>
        Assert.Equal($" ({Units.FormatBytes(100 * MiB)}/s, about 3 minutes left)", Throughput.Describe(100 * MiB, TimeSpan.FromMinutes(3)));
    [Fact]
    public void IdleSamplesReduceTheRateWithoutInventingCompletedBytes()
    {
        var meter = new ThroughputMeter();
        meter.Sample("copying", 0, 1000 * MiB, TimeSpan.Zero);
        meter.Sample("copying", 100 * MiB, 1000 * MiB, TimeSpan.FromSeconds(1));
        var (rate, left) = meter.Sample("copying", 100 * MiB, 1000 * MiB, TimeSpan.FromSeconds(5));
        Assert.Equal(20.0 * MiB, rate!.Value, 3);
        Assert.Equal(45, left!.Value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidBenchmarkRatesAreRejected(double rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Throughput.EstimateMove(GiB, rate));

}
