using OpenPartition.Discovery;
using OpenPartition.Model;
using OpenPartition.Operations;

namespace OpenPartition.Tests;

/// <summary>
/// The planner on the shape found on the Azure field VM: C: boxed in by a primary (I:) and an
/// extended partition with logical drives, free space at the end of the disk.
/// </summary>
public class PlannerTests
{
    private const long MiB = Units.MiB;
    private const long GiB = 1024 * MiB;

    private static PartitionInfo Part(int number, long offset, long size, char? letter, bool logical = false, bool boot = false, PartitionRole role = PartitionRole.Data)
    {
        var volume = role == PartitionRole.Extended ? null : new VolumeInfo
        {
            Path = $@"\\?\Volume{{{number:D8}-0000-0000-0000-000000000000}}\",
            DriveLetter = letter,
            FileSystem = "NTFS",
            BitLocker = BitLockerState.NotEncrypted,
            Capacity = size,
            FreeSpace = size / 2,
        };
        var p = new PartitionInfo
        {
            Number = number,
            Offset = offset,
            Size = size,
            Role = role,
            MbrType = role == PartitionRole.Extended ? MbrTypes.ExtendedLba : (byte)0x07,
            IsLogical = logical,
            IsBoot = boot,
            Volume = volume,
        };
        return p with { Issues = Classifier.ForPartition(p) };
    }

    /// <summary>System Reserved 500 MiB, C: 50 GiB, I: 10 GiB, extended (E: 25, L: 10, K: 30 GiB), then free space.</summary>
    private static DiskInfo AzureLike()
    {
        var c = 501 * MiB;
        var i = c + 50 * GiB;
        var ext = i + 10 * GiB;
        var partitions = new List<PartitionInfo>
        {
            Part(1, MiB, 500 * MiB, null) with { IsSystem = true },
            Part(2, c, 50 * GiB, 'C', boot: true),
            Part(3, i, 10 * GiB, 'I'),
            Part(0, ext, 65 * GiB + 3 * MiB, null, role: PartitionRole.Extended),
            Part(4, ext + MiB, 25 * GiB, 'E', logical: true) with { EbrOffset = ext },
            Part(5, ext + 25 * GiB + 2 * MiB, 10 * GiB, 'L', logical: true) with { EbrOffset = ext + 25 * GiB + MiB },
            Part(6, ext + 35 * GiB + 3 * MiB, 30 * GiB, 'K', logical: true) with { EbrOffset = ext + 35 * GiB + 2 * MiB },
        };
        partitions = partitions.Select(p => p with { Issues = Classifier.ForPartition(p) }).ToList();
        const long size = 256 * GiB;
        return new DiskInfo
        {
            Number = 0,
            Size = size,
            LogicalSectorSize = 512,
            Style = PartitionStyle.Mbr,
            UsableStart = 512,
            UsableEnd = size,
            Partitions = partitions,
            FreeExtents = FreeSpace.Compute(512, size, partitions),
        };
    }

    private static PartitionInfo Letter(DiskInfo disk, char letter) => disk.Partitions.Single(p => p.Volume?.DriveLetter == letter);

    private static PartitionInfo Extended(DiskInfo disk) => disk.Partitions.Single(p => p.Role == PartitionRole.Extended);

    [Fact]
    public void BootVolumeCanBeExtendedButNotMoved()
    {
        var disk = AzureLike();
        var c = Letter(disk, 'C');
        Assert.NotEmpty(Planner.MoveBlockers(disk, c));
        Assert.Contains(Planner.ExtendBlockers(disk, c), r => r.Contains("free space", StringComparison.Ordinal));
    }

    [Fact]
    public void PackedLogicalDrivesHaveNoRoomToMoveButTheirExtendedPartitionDoes()
    {
        var disk = AzureLike();
        foreach (var letter in "ELK")
        {
            Assert.Equal(["There is no free space next to it to move into."], Planner.MoveBlockers(disk, Letter(disk, letter)));
        }
        Assert.Empty(Planner.MoveBlockers(disk, Extended(disk)));
    }

    /// <summary>
    /// Extended partition 1..11 GiB holding E: (first, 1 GiB at +1 MiB), L: (1 GiB at +3 GiB,
    /// EBR 1 MiB before) and K: (1 GiB at +6 GiB, EBR 1 MiB before), with free space between.
    /// </summary>
    private static DiskInfo LogicalsWithGaps()
    {
        const long ext = GiB;
        var partitions = new List<PartitionInfo>
        {
            Part(0, ext, 10 * GiB, null, role: PartitionRole.Extended),
            Part(1, ext + MiB, GiB, 'E', logical: true) with { EbrOffset = ext },
            Part(2, ext + 3 * GiB, GiB, 'L', logical: true) with { EbrOffset = ext + 3 * GiB - MiB },
            Part(3, ext + 6 * GiB, GiB, 'K', logical: true) with { EbrOffset = ext + 6 * GiB - MiB },
        };
        const long size = 16 * GiB;
        return new DiskInfo
        {
            Number = 1,
            Size = size,
            LogicalSectorSize = 512,
            Style = PartitionStyle.Mbr,
            UsableStart = 512,
            UsableEnd = size,
            Partitions = partitions,
            FreeExtents = FreeSpace.Compute(512, size, partitions),
        };
    }

    [Fact]
    public void LogicalDriveMovesBetweenItsNeighboursKeepingRoomForEbrs()
    {
        const long ext = GiB;
        var disk = LogicalsWithGaps();
        // The first drive's EBR stays at the container start; it may go up to the next EBR.
        Assert.Equal(new MoveRange(ext + MiB, ext + 2 * GiB - MiB), Planner.Range(disk, Letter(disk, 'E')));
        // A later drive takes its EBR along, 1 MiB in front, so it can't start right after E:.
        Assert.Equal(new MoveRange(ext + GiB + 2 * MiB, ext + 5 * GiB - MiB), Planner.Range(disk, Letter(disk, 'L')));
        // The last one may go up to the end of the extended partition.
        Assert.Equal(new MoveRange(ext + 4 * GiB + MiB, ext + 9 * GiB), Planner.Range(disk, Letter(disk, 'K')));
        Assert.Throws<PlanException>(() => Planner.Move(disk, Letter(disk, 'L'), ext + 5 * GiB));
        Assert.Throws<PlanException>(() => Planner.Move(disk, Letter(disk, 'L'), ext + GiB + MiB));
    }

    [Fact]
    public void MovedLogicalDriveTakesItsEbrAlongInThePreview()
    {
        const long ext = GiB;
        var disk = LogicalsWithGaps();
        var move = Planner.Move(disk, Letter(disk, 'L'), ext + GiB + 2 * MiB);
        Assert.Equal(ext + 3 * GiB, move.From);
        Assert.Equal(GiB, move.Length);
        disk = Planner.Apply(disk, move);
        var l = Letter(disk, 'L');
        Assert.Equal((ext + GiB + 2 * MiB, ext + GiB + MiB), (l.Offset, l.EbrOffset));
        Assert.Equal(ext + MiB, Letter(disk, 'E').Offset);
        Assert.Equal(ext + 6 * GiB, Letter(disk, 'K').Offset);
        // K: can now come down to just after L:'s new place.
        Assert.Equal(ext + 2 * GiB + 3 * MiB, Planner.Range(disk, Letter(disk, 'K'))!.Min);

        // Then the whole extended partition: every EBR moves with it, the first one included.
        var extended = Extended(disk);
        disk = Planner.Apply(disk, Planner.Move(disk, extended, 4 * GiB));
        var delta = 3 * GiB;
        Assert.Equal(4 * GiB, Letter(disk, 'E').EbrOffset);
        Assert.Equal(ext + GiB + MiB + delta, Letter(disk, 'L').EbrOffset);
    }

    [Fact]
    public void TwoMovesOfOneLogicalDriveFoldIntoOne()
    {
        const long ext = GiB;
        var disk = LogicalsWithGaps();
        var first = Planner.Move(disk, Letter(disk, 'K'), ext + 7 * GiB);
        var second = Planner.Move(Planner.Apply(disk, first), Letter(Planner.Apply(disk, first), 'K'), ext + 8 * GiB);
        var queue = Planner.Append(disk, Planner.Append(disk, [], first), second);
        var only = Assert.IsType<MoveStep>(Assert.Single(queue));
        Assert.Equal((ext + 6 * GiB, ext + 8 * GiB), (only.From, only.To));
    }

    [Fact]
    public void LogicalDriveWithAnUnknownEbrIsNotMoved()
    {
        var disk = LogicalsWithGaps();
        disk = disk with { Partitions = disk.Partitions.Select(p => p.Volume?.DriveLetter == 'L' ? p with { EbrOffset = null } : p).ToList() };
        Assert.Null(Planner.Range(disk, Letter(disk, 'L')));
        Assert.Null(Planner.Range(disk, Letter(disk, 'K')));
    }

    [Fact]
    public void FreeingTheSpaceAfterCTakesTwoMovesThenCCanGrow()
    {
        var disk = AzureLike();
        var extended = Extended(disk);
        var range = Planner.Range(disk, extended)!;
        Assert.Equal((disk.Size - extended.Size) / MiB * MiB, range.Max);

        var moveExtended = Planner.Move(disk, extended, range.Max);
        disk = Planner.Apply(disk, moveExtended);
        var delta = moveExtended.To - moveExtended.From;
        Assert.All(disk.Partitions.Where(p => p.IsLogical), p => Assert.Equal(AzureLike().Partitions.Single(o => o.Number == p.Number).Offset + delta, p.Offset));

        var i = Letter(disk, 'I');
        var moveI = Planner.Move(disk, i, Planner.Range(disk, i)!.Max);
        disk = Planner.Apply(disk, moveI);
        Assert.Equal(Extended(disk).Offset, Letter(disk, 'I').End);

        var c = Letter(disk, 'C');
        Assert.Empty(Planner.ExtendBlockers(disk, c));
        var max = Planner.MaxSize(disk, c);
        Assert.Equal(Letter(disk, 'I').Offset - c.Offset, max);
        disk = Planner.Apply(disk, Planner.Extend(disk, c, max));
        Assert.Equal(max, Letter(disk, 'C').Size);
        Assert.DoesNotContain(disk.FreeExtents, f => f.Offset < Letter(disk, 'I').Offset);
    }

    [Fact]
    public void TargetsMustBeAlignedAndInsideTheFreeSpace()
    {
        var disk = AzureLike();
        var extended = Extended(disk);
        var range = Planner.Range(disk, extended)!;
        Assert.Throws<PlanException>(() => Planner.Move(disk, extended, range.Max + MiB));
        Assert.Throws<PlanException>(() => Planner.Move(disk, extended, range.Max - 512));
        Assert.Throws<PlanException>(() => Planner.Move(disk, extended, extended.Offset));
        var i = Letter(disk, 'I');
        Assert.Null(Planner.Range(disk, i));
        Assert.Contains(Planner.MoveBlockers(disk, i), r => r.Contains("no free space", StringComparison.Ordinal));
    }

    [Fact]
    public void ABlockedLogicalDriveBlocksItsExtendedPartition()
    {
        var disk = AzureLike();
        var withPageFile = disk.Partitions.Select(p => p.Volume?.DriveLetter == 'L'
            ? p with { Volume = p.Volume with { HasPageFile = true } }
            : p).ToList();
        withPageFile = withPageFile.Select(p => p with { Issues = Classifier.ForPartition(p) }).ToList();
        disk = disk with { Partitions = withPageFile };
        Assert.Contains(Planner.MoveBlockers(disk, Extended(disk)), r => r.StartsWith("L:", StringComparison.Ordinal));
    }

    [Fact]
    public void RealDisksAreWritableOnlyOnAzure()
    {
        var disk = AzureLike() with { BusType = "SAS" };
        SystemSnapshot On(HostEnvironment environment) => new()
        {
            CollectedAt = DateTimeOffset.UnixEpoch,
            MachineName = "test",
            OsVersion = "10.0",
            IsElevated = true,
            Environment = environment,
            Disks = [disk],
        };
        Assert.Equal(WriteVerdict.NeedsSnapshot, WriteGate.For(On(HostEnvironment.Azure), disk));
        Assert.Equal(WriteVerdict.Refused, WriteGate.For(On(HostEnvironment.Physical), disk));
        Assert.Equal(WriteVerdict.Refused, WriteGate.For(On(HostEnvironment.HyperV), disk));
        var vhd = disk with { BusType = WriteGate.VirtualBusType };
        Assert.Equal(WriteVerdict.Allowed, WriteGate.For(On(HostEnvironment.Physical), vhd));
    }

    [Fact]
    public void EngineDiskIdMatchesTheJournal()
    {
        var mbr = AzureLike() with { MbrSignature = 0xCF892F14 };
        Assert.Equal("142F89CF4D4252000000000000000000", Engine.DiskIdOf(mbr));
    }

    [Fact]
    public void LockFailureNamesTheVolume()
    {
        var disk = AzureLike();
        var e = Letter(disk, 'E');
        var unlettered = disk with { Partitions = disk.Partitions.Select(p => p == e ? p with { Volume = p.Volume! with { DriveLetter = null, Label = "Apps" } } : p).ToList() };
        var snapshot = new SystemSnapshot { CollectedAt = DateTimeOffset.UnixEpoch, MachineName = "t", OsVersion = "10", IsElevated = true, Disks = [unlettered] };
        var error = $"refused: could not lock {e.Volume!.Path}: files on it are open. Close the programs";
        var explained = VolumeUsers.Explain(error, snapshot);
        Assert.StartsWith("'Apps' is in use", explained, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", explained, StringComparison.Ordinal);
        Assert.Equal("disk I/O failed", VolumeUsers.Explain("disk I/O failed", snapshot));
    }

    [Fact]
    public void LastLogicalDriveGrowsAfterItsExtendedPartition()
    {
        var disk = AzureLike();
        var k = Letter(disk, 'K');
        Assert.Contains(Planner.ExtendBlockers(disk, k), r => r.Contains("Extend the extended partition first", StringComparison.Ordinal));
        Assert.Contains(Planner.ExtendBlockers(disk, Letter(disk, 'E')), r => r.Contains("no free space directly after", StringComparison.Ordinal));

        var extended = Extended(disk);
        Assert.Empty(Planner.ExtendBlockers(disk, extended));
        var grow = Planner.Extend(disk, extended, Planner.MaxSize(disk, extended));
        Assert.True(grow.Container);
        Assert.Equal(disk.Size, grow.Offset + grow.NewSize);
        disk = Planner.Apply(disk, grow);
        Assert.Equal(Letter(AzureLike(), 'K').Offset, Letter(disk, 'K').Offset);

        k = Letter(disk, 'K');
        Assert.Empty(Planner.ExtendBlockers(disk, k));
        var extendK = Planner.Extend(disk, k, Planner.MaxSize(disk, k));
        Assert.False(extendK.Container);
        disk = Planner.Apply(disk, extendK);
        Assert.Equal(Extended(disk).End, Letter(disk, 'K').End);
        Assert.Empty(disk.FreeExtents);
    }

    [Fact]
    public void MovingThePartitionAgainMergesIntoOneMove()
    {
        var original = AzureLike();
        var extended = Extended(original);
        var first = Planner.Move(original, extended, Planner.Range(original, extended)!.Max);
        var queue = Planner.Append(original, [], first);
        var after = Planner.Apply(original, first);
        var target = extended.Offset + 10 * GiB;
        var second = Planner.Move(after, Extended(after), target);
        queue = Planner.Append(original, queue, second);
        var merged = Assert.IsType<MoveStep>(Assert.Single(queue));
        Assert.Equal((extended.Offset, target), (merged.From, merged.To));

        var back = Planner.Move(Planner.Apply(original, merged), Extended(Planner.Apply(original, merged)), extended.Offset);
        Assert.Empty(Planner.Append(original, queue, back));
    }

    [Fact]
    public void MovesOfDifferentPartitionsAreNotMerged()
    {
        var original = AzureLike();
        var extended = Extended(original);
        var moveExtended = Planner.Move(original, extended, Planner.Range(original, extended)!.Max);
        var after = Planner.Apply(original, moveExtended);
        var i = Letter(after, 'I');
        var moveI = Planner.Move(after, i, Planner.Range(after, i)!.Max);
        Assert.Equal(2, Planner.Append(original, Planner.Append(original, [], moveExtended), moveI).Count);
    }

    [Fact]
    public void TwoExtendsOfTheSameVolumeMerge()
    {
        var original = AzureLike();
        var extended = Extended(original);
        var grow1 = Planner.Extend(original, extended, extended.Size + 10 * GiB);
        var grow2 = Planner.Extend(Planner.Apply(original, grow1), Extended(Planner.Apply(original, grow1)), extended.Size + 20 * GiB);
        var merged = Assert.IsType<ExtendStep>(Assert.Single(Planner.Append(original, [grow1], grow2)));
        Assert.Equal((extended.Size, extended.Size + 20 * GiB), (merged.OldSize, merged.NewSize));
    }

    [Theory]
    [InlineData(62L, 191L, 65L, true)]   // the Azure case: far right, never overlaps
    [InlineData(62L, 135L, 65L, true)]   // the merged plan: 73 GB right, still no overlap
    [InlineData(62L, 100L, 65L, false)]  // overlapping: the start has been overwritten
    [InlineData(100L, 40L, 65L, false)]
    public void OnlyMovesThatNeverOverwriteTheirSourceCanBeUndone(long source, long target, long length, bool canUndo) =>
        Assert.Equal(canUndo, new PendingMove("j", "id", 1, source * GiB, target * GiB, length * GiB, "0/1").CanUndo);
}
