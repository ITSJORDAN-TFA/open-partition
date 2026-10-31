using OpenPartition.Discovery;
using OpenPartition.Model;
using OpenPartition.Operations;

namespace OpenPartition.Tests;

/// <summary>
/// Plan files (<c>opm plan</c> / <c>opm apply</c>) on the Azure field shape: SYS boxed in by a
/// primary (I:) and an extended partition with logical drives, free space at the end.
/// </summary>
public class PlanFileTests
{
    private const long MiB = Units.MiB;
    private const long GiB = 1024 * MiB;
    private static readonly DateTimeOffset Made = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static PartitionInfo Part(int number, long offset, long size, char? letter, bool logical = false, PartitionRole role = PartitionRole.Data, BitLockerState bitLocker = BitLockerState.NotEncrypted)
    {
        var p = new PartitionInfo
        {
            Number = number,
            Offset = offset,
            Size = size,
            Role = role,
            MbrType = role == PartitionRole.Extended ? MbrTypes.ExtendedLba : (byte)0x07,
            IsLogical = logical,
            Volume = role == PartitionRole.Extended ? null : new VolumeInfo
            {
                Path = $@"\\?\Volume{{{number:D8}-0000-0000-0000-000000000000}}\",
                DriveLetter = letter,
                FileSystem = "NTFS",
                BitLocker = bitLocker,
                Capacity = size,
                FreeSpace = size / 2,
            },
        };
        return p with { Issues = Classifier.ForPartition(p) };
    }

    /// <summary>D: 20 GiB, I: 10 GiB, extended (E: 10, L: 10 GiB), then free space; MBR, 128 GiB.</summary>
    private static DiskInfo Disk(int number = 1, long size = 128 * GiB, BitLockerState iBitLocker = BitLockerState.NotEncrypted)
    {
        var i = MiB + (20 * GiB);
        var ext = i + (10 * GiB);
        List<PartitionInfo> partitions =
        [
            Part(1, MiB, 20 * GiB, 'D'),
            Part(2, i, 10 * GiB, 'I', bitLocker: iBitLocker),
            Part(0, ext, (20 * GiB) + (2 * MiB), null, role: PartitionRole.Extended),
            Part(3, ext + MiB, 10 * GiB, 'E', logical: true),
            Part(4, ext + (10 * GiB) + (2 * MiB), 10 * GiB, 'L', logical: true),
        ];
        return new DiskInfo
        {
            Number = number,
            BusType = WriteGate.VirtualBusType,
            Size = size,
            LogicalSectorSize = 512,
            Style = PartitionStyle.Mbr,
            MbrSignature = 0x5EED1234,
            UsableStart = 512,
            UsableEnd = size,
            Partitions = partitions,
            FreeExtents = FreeSpace.Compute(512, size, partitions),
        };
    }

    private static SystemSnapshot Snapshot(params DiskInfo[] disks) => new()
    {
        CollectedAt = Made,
        MachineName = "TEST",
        OsVersion = "10.0",
        IsElevated = true,
        Disks = disks,
    };

    private static PartitionInfo Letter(DiskInfo disk, char letter) => disk.Partitions.Single(p => p.Volume?.DriveLetter == letter);

    private static PartitionInfo Extended(DiskInfo disk) => disk.Partitions.Single(p => p.Role == PartitionRole.Extended);

    private static MoveStep MoveToEnd(DiskInfo d, PartitionInfo p) => Planner.Move(d, p, Planner.Range(d, p)!.Max);

    /// <summary>The field plan: extended partition to the end, I: after it, then D: grows into the gap.</summary>
    private static PlanFile FieldPlan(SystemSnapshot live) => PlanFile.New(Made, "TEST")
        .Add(live, 1, d => MoveToEnd(d, Extended(d)))
        .Add(live, 1, d => MoveToEnd(d, Letter(d, 'I')))
        .Add(live, 1, d => Planner.Extend(d, Letter(d, 'D'), Planner.MaxSize(d, Letter(d, 'D'))));

    [Fact]
    public void RoundTripKeepsDisksAndSteps()
    {
        var plan = FieldPlan(Snapshot(Disk()));
        var json = plan.ToJson();
        var back = PlanFile.FromJson(json);

        Assert.Contains("\"format\": 1", json, StringComparison.Ordinal);
        Assert.Equal(plan.CreatedAt, back.CreatedAt);
        Assert.Equal(plan.MachineName, back.MachineName);
        Assert.Equal(plan.Disks, back.Disks);
        Assert.Equal(plan.Steps, back.Steps);
        Assert.Equal(json, back.ToJson());
    }

    [Fact]
    public void StepsAreCheckedAgainstTheLayoutAfterEarlierSteps()
    {
        var live = Snapshot(Disk());
        var empty = PlanFile.New(Made, "TEST");
        // On the disk as it is now, D: has no room to grow...
        Assert.Throws<PlanException>(() => empty.Add(live, 1, d => Planner.Extend(d, Letter(d, 'D'), Planner.MaxSize(d, Letter(d, 'D')))));
        // ...but after the two moves it does, and it grows up to where I: now starts.
        var plan = FieldPlan(live);
        Assert.Equal(3, plan.Steps.Count);
        var extend = Assert.IsType<ExtendStep>(plan.Steps[2]);
        var after = plan.Project(Disk());
        Assert.Equal(Letter(after, 'I').Offset, Letter(after, 'D').End);
        Assert.Equal(extend.NewSize, Letter(after, 'D').Size);
        Assert.Single(plan.Disks);
        Assert.Equal(PlanFile.Fingerprint(Disk()), plan.Disks[0].Fingerprint);
    }

    [Fact]
    public void RepeatedMovesAndExtendsMergeLikeTheGuiQueue()
    {
        var live = Snapshot(Disk());
        var plan = PlanFile.New(Made, "TEST")
            .Add(live, 1, d => Planner.Move(d, Extended(d), Extended(d).Offset + GiB))
            .Add(live, 1, d => MoveToEnd(d, Extended(d)));
        var move = Assert.IsType<MoveStep>(Assert.Single(plan.Steps));
        Assert.Equal(Extended(Disk()).Offset, move.From);
        Assert.Equal(Planner.Range(Disk(), Extended(Disk()))!.Max, move.To);

        // Moving it back where it started cancels the move.
        plan = plan.Add(live, 1, d => Planner.Move(d, Extended(d), Extended(Disk()).Offset));
        Assert.Empty(plan.Steps);

        // Two extends of the same partition become one, from the original size.
        plan = FieldPlan(live);
        var d0 = plan.Project(Disk());
        var full = ((ExtendStep)plan.Steps[2]).NewSize;
        plan = plan with { Steps = [.. plan.Steps.Take(2)] };
        plan = plan.Add(live, 1, d => Planner.Extend(d, Letter(d, 'D'), Letter(d, 'D').Size + GiB))
            .Add(live, 1, d => Planner.Extend(d, Letter(d, 'D'), full));
        Assert.Equal(3, plan.Steps.Count);
        var merged = Assert.IsType<ExtendStep>(plan.Steps[2]);
        Assert.Equal(20 * GiB, merged.OldSize);
        Assert.Equal(full, merged.NewSize);
        Assert.Equal(Letter(d0, 'D').Size, Letter(plan.Project(Disk()), 'D').Size);
    }

    [Fact]
    public void ValidateReturnsTheStepsWhenNothingChanged()
    {
        var plan = FieldPlan(Snapshot(Disk()));
        Assert.Equal(plan.Steps, plan.Validate(Snapshot(Disk())));
    }

    [Fact]
    public void ValidateRefusesWhenTheLayoutChanged()
    {
        var plan = FieldPlan(Snapshot(Disk()));
        var changed = Disk();
        var l = Letter(changed, 'L');
        changed = changed with { Partitions = [.. changed.Partitions.Where(p => p != l), l with { Size = l.Size - GiB }] };

        var e = Assert.Throws<PlanException>(() => plan.Validate(Snapshot(changed)));
        Assert.Contains("changed since this plan was made", e.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRefusesWhenTheDiskIsMissingOrADifferentSize()
    {
        var plan = FieldPlan(Snapshot(Disk()));
        Assert.Throws<PlanException>(() => plan.Validate(Snapshot()));
        Assert.Throws<PlanException>(() => plan.Validate(Snapshot(Disk(size: 256 * GiB))));
        Assert.Throws<PlanException>(() => plan.Validate(Snapshot(Disk() with { MbrSignature = 0x0BADF00D })));
    }

    [Fact]
    public void ValidateRefusesAStepThatIsNoLongerAllowed()
    {
        // Same layout, but I: was encrypted since the plan was made: moving it is refused now.
        var plan = FieldPlan(Snapshot(Disk()));
        var e = Assert.Throws<PlanException>(() => plan.Validate(Snapshot(Disk(iBitLocker: BitLockerState.Encrypted))));
        Assert.StartsWith("Step 2 ", e.Message, StringComparison.Ordinal);
        Assert.Contains("no longer possible", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateFollowsADiskThatWasRenumbered()
    {
        var plan = FieldPlan(Snapshot(Disk()));
        var steps = plan.Validate(Snapshot(Disk(number: 3)));
        Assert.All(steps, s => Assert.Equal(3, s.DiskNumber));
        Assert.Equal(plan.Steps.Select(s => s with { DiskNumber = 3 }), steps);
    }

    [Fact]
    public void AddingRefusesWhenTheDiskChangedOrWasRenumbered()
    {
        var plan = PlanFile.New(Made, "TEST").Add(Snapshot(Disk()), 1, d => MoveToEnd(d, Extended(d)));
        Assert.Throws<PlanException>(() => plan.Add(Snapshot(Disk(number: 2)), 2, d => MoveToEnd(d, Letter(d, 'I'))));
        var moved = Planner.Apply(Disk(), plan.Steps[0]);
        Assert.Throws<PlanException>(() => plan.Add(Snapshot(moved), 1, d => MoveToEnd(d, Letter(d, 'I'))));
    }

    [Fact]
    public void AnEmptyPlanCannotBeApplied()
    {
        Assert.Throws<PlanException>(() => PlanFile.New(Made, "TEST").Validate(Snapshot(Disk())));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "format": 2, "disks": [], "steps": [] }""")]
    [InlineData("""{ "format": 1, "disks": [], "steps": [ { "kind": "move", "disk": 1, "from": 1, "to": 2, "length": 3 } ] }""")]
    [InlineData("""{ "format": 1, "disks": [ { "number": 1, "diskId": "AB", "size": 1, "fingerprint": "CD" } ], "steps": [ { "kind": "move", "disk": 1, "from": 1 } ] }""")]
    [InlineData("""{ "format": 1, "disks": [ { "number": 1, "diskId": "AB", "size": 1, "fingerprint": "CD" } ], "steps": [ { "kind": "format", "disk": 1 } ] }""")]
    public void DamagedOrNewerPlanFilesAreRefused(string json)
    {
        Assert.Throws<PlanException>(() => PlanFile.FromJson(json));
    }
}
