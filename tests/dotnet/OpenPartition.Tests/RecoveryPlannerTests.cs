using OpenPartition.Discovery;
using OpenPartition.Model;
using OpenPartition.Operations;

namespace OpenPartition.Tests;

/// <summary>
/// "C: boxed in by recovery": a GPT (Hyper-V Gen2 / Azure) OS disk grown from 127 GiB to
/// 256 GiB, with the Windows Recovery partition right after C: and the new space after it.
/// The harness layout <c>gpt-512e-recovery-after-c</c> is the same shape in miniature.
/// </summary>
public class RecoveryPlannerTests
{
    private const long MiB = Units.MiB;
    private const long GiB = 1024 * MiB;
    private const int DiskNumber = 0;

    private static readonly WinReInfo WinReOnRecovery = new()
    {
        Status = WinReStatus.Enabled,
        Location = new WinReLocation(DiskNumber, 4, @"\\?\GLOBALROOT\device\harddisk0\partition4\Recovery\WindowsRE"),
    };

    private static DiskInfo RecoveryAfterC(WinReInfo? winRe)
    {
        var c = 117 * MiB;
        var recovery = c + 126 * GiB;
        var partitions = new List<PartitionInfo>
        {
            new() { Number = 1, Offset = MiB, Size = 100 * MiB, Role = PartitionRole.EfiSystem, GptType = GptTypes.EfiSystem, IsSystem = true },
            new() { Number = 2, Offset = 101 * MiB, Size = 16 * MiB, Role = PartitionRole.MicrosoftReserved, GptType = GptTypes.MicrosoftReserved },
            new()
            {
                Number = 3, Offset = c, Size = 126 * GiB, Role = PartitionRole.Data, GptType = GptTypes.BasicData, IsBoot = true,
                Volume = new VolumeInfo { Path = @"\\?\Volume{00000003-0000-0000-0000-000000000000}\", DriveLetter = 'C', FileSystem = "NTFS", BitLocker = BitLockerState.NotEncrypted, Capacity = 126 * GiB, FreeSpace = 40 * GiB },
            },
            new()
            {
                Number = 4, Offset = recovery, Size = 750 * MiB, Role = PartitionRole.Recovery, GptType = GptTypes.Recovery,
                GptAttributes = 0x8000_0000_0000_0001,
                HostsWinRe = winRe?.Location?.PartitionNumber == 4,
                Volume = new VolumeInfo { Path = @"\\?\Volume{00000004-0000-0000-0000-000000000000}\", FileSystem = "NTFS", BitLocker = BitLockerState.NotEncrypted, Capacity = 750 * MiB, FreeSpace = 200 * MiB },
            },
        };
        partitions = partitions.Select(p => p with { Issues = Classifier.ForPartition(p, winRe, DiskNumber) }).ToList();
        const long size = 256 * GiB;
        var usableStart = 34 * 512;
        var usableEnd = size - 33 * 512;
        return new DiskInfo
        {
            Number = DiskNumber,
            Size = size,
            LogicalSectorSize = 512,
            PhysicalSectorSize = 4096,
            Style = PartitionStyle.Gpt,
            UsableStart = usableStart,
            UsableEnd = usableEnd,
            Partitions = partitions,
            FreeExtents = FreeSpace.Compute(usableStart, usableEnd, partitions),
        };
    }

    private static PartitionInfo C(DiskInfo disk) => disk.Partitions.Single(p => p.Volume?.DriveLetter == 'C');

    private static PartitionInfo Recovery(DiskInfo disk) => disk.Partitions.Single(p => p.Role == PartitionRole.Recovery);

    [Fact]
    public void CIsBoxedInByRecovery()
    {
        var disk = RecoveryAfterC(WinReOnRecovery);
        Assert.Contains(Planner.ExtendBlockers(disk, C(disk)), r => r.Contains("no free space directly after", StringComparison.Ordinal));
        Assert.NotEmpty(Planner.MoveBlockers(disk, C(disk)));
    }

    [Fact]
    public void MovingRecoveryToTheEndLetsCGrowIntoAllTheNewSpace()
    {
        var disk = RecoveryAfterC(WinReOnRecovery);
        var recovery = Recovery(disk);
        Assert.Empty(Planner.MoveBlockers(disk, recovery));

        var range = Planner.Range(disk, recovery)!;
        Assert.Equal(C(disk).End, range.Min);
        Assert.Equal((disk.UsableEnd - recovery.Size) / MiB * MiB, range.Max);

        var move = Planner.Move(disk, recovery, range.Max);
        Assert.True(move.WinRe);
        Assert.Contains("re-register the Windows Recovery Environment", move.Describe(), StringComparison.Ordinal);
        Assert.Equal($"the recovery partition at {Units.FormatBytes(recovery.Offset)}", move.What);

        disk = Planner.Apply(disk, move);
        Assert.Equal(range.Max, Recovery(disk).Offset);
        // The partition keeps its type and attributes in the plan, as the engine keeps them on disk.
        Assert.Equal(GptTypes.Recovery, Recovery(disk).GptType);
        Assert.Equal(0x8000_0000_0000_0001UL, Recovery(disk).GptAttributes);

        var c = C(disk);
        Assert.Empty(Planner.ExtendBlockers(disk, c));
        var max = Planner.MaxSize(disk, c);
        Assert.Equal(Recovery(disk).Offset - c.Offset, max);
        disk = Planner.Apply(disk, Planner.Extend(disk, c, max));
        Assert.Equal(Recovery(disk).Offset, C(disk).End);
    }

    [Fact]
    public void RecoveryWithoutWinReMovesWithoutTheHook()
    {
        var disk = RecoveryAfterC(new WinReInfo { Status = WinReStatus.Disabled });
        var recovery = Recovery(disk);
        Assert.Empty(Planner.MoveBlockers(disk, recovery));
        Assert.False(Planner.Move(disk, recovery, Planner.Range(disk, recovery)!.Max).WinRe);
    }

    [Fact]
    public void RecoveryIsRefusedWhenWinReCannotBeRead()
    {
        var disk = RecoveryAfterC(WinReInfo.Unknown("reagentc /info failed with exit code 5"));
        var recovery = Recovery(disk);
        var reasons = Planner.MoveBlockers(disk, recovery);
        Assert.Contains(reasons, r => r.Contains("could not be re-registered", StringComparison.Ordinal));
        Assert.Throws<PlanException>(() => Planner.Move(disk, recovery, Planner.Range(disk, recovery)!.Max));
    }

    [Fact]
    public void RecoveryCanStillOnlyBeMoved()
    {
        var disk = RecoveryAfterC(WinReOnRecovery);
        Assert.NotEmpty(Planner.ExtendBlockers(disk, Recovery(disk)));
    }

    [Fact]
    public void TwoMovesOfTheRecoveryPartitionMergeAndKeepTheHook()
    {
        var original = RecoveryAfterC(WinReOnRecovery);
        var recovery = Recovery(original);
        var first = Planner.Move(original, recovery, recovery.Offset + GiB);
        var after = Planner.Apply(original, first);
        var second = Planner.Move(after, Recovery(after), Planner.Range(after, Recovery(after))!.Max);
        var queue = Planner.Append(original, Planner.Append(original, [], first), second);
        var merged = Assert.IsType<MoveStep>(Assert.Single(queue));
        Assert.Equal(recovery.Offset, merged.From);
        Assert.True(merged.WinRe);
    }
}
