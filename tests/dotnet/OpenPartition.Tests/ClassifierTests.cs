using OpenPartition.Discovery;
using OpenPartition.Model;

namespace OpenPartition.Tests;

public class ClassifierTests
{
    private static PartitionInfo Data(VolumeInfo? volume = null) => new()
    {
        Number = 2,
        Offset = 16 * Units.MiB,
        Size = 1024 * Units.MiB,
        Role = PartitionRole.Data,
        GptType = GptTypes.BasicData,
        Volume = volume,
    };

    private static VolumeInfo Volume() => new()
    {
        Path = @"\\?\volume{00000000-0000-0000-0000-000000000001}\",
        FileSystem = "NTFS",
        BitLocker = BitLockerState.NotEncrypted,
    };

    private static DiskInfo Disk(params PartitionInfo[] partitions) => new()
    {
        Number = 1,
        Size = 4096 * Units.MiB,
        LogicalSectorSize = 512,
        Style = PartitionStyle.Gpt,
        Partitions = partitions,
    };

    private static bool Blocks(IEnumerable<Issue> issues, string code, OperationKind op) =>
        issues.Any(i => i.Code == code && i.Severity == IssueSeverity.Blocker && i.BlocksOperation(op));

    [Fact]
    public void PlainDataVolumeAllowsEverything()
    {
        var p = Data(Volume());
        p = p with { Issues = Classifier.ForPartition(p) };
        var disk = Disk(p);
        disk = disk with { Issues = Classifier.ForDisk(disk) };
        Assert.Empty(p.Issues);
        Assert.Empty(disk.Issues);
        Assert.True(disk.Allows(p, OperationKind.Move));
    }

    [Theory]
    [InlineData(PartitionRole.EfiSystem, "partition.efi")]
    [InlineData(PartitionRole.MicrosoftReserved, "partition.msr")]
    [InlineData(PartitionRole.Recovery, "partition.recovery")]
    [InlineData(PartitionRole.Unknown, "partition.unknown-type")]
    [InlineData(PartitionRole.MoveInProgress, "partition.move-in-progress")]
    public void SpecialPartitionsBlockEveryOperation(PartitionRole role, string code)
    {
        var issues = Classifier.ForPartition(Data() with { Role = role });
        foreach (var op in new[] { OperationKind.Move, OperationKind.Delete, OperationKind.Extend, OperationKind.Shrink, OperationKind.Format })
        {
            Assert.True(Blocks(issues, code, op), $"{code} should block {op}");
        }
    }

    [Fact]
    public void ExtendedPartitionCanOnlyBeMovedAsAWhole()
    {
        var issues = Classifier.ForPartition(Data() with { Role = PartitionRole.Extended });
        Assert.DoesNotContain(issues, i => i.BlocksOperation(OperationKind.Move) || i.BlocksOperation(OperationKind.Extend));
        foreach (var op in new[] { OperationKind.Delete, OperationKind.Shrink, OperationKind.Format })
        {
            Assert.True(Blocks(issues, "partition.extended", op), op.ToString());
        }
    }

    [Fact]
    public void LogicalDriveCannotBeMovedOnItsOwnButCanBeExtended()
    {
        var issues = Classifier.ForPartition(Data(Volume()) with { IsLogical = true });
        Assert.True(Blocks(issues, "partition.logical", OperationKind.Move));
        Assert.False(Blocks(issues, "partition.logical", OperationKind.Extend));
    }

    [Fact]
    public void BootVolumeMayResizeButNotMoveDeleteOrFormat()
    {
        var issues = Classifier.ForPartition(Data(Volume()) with { IsBoot = true });
        Assert.True(Blocks(issues, "partition.boot", OperationKind.Move));
        Assert.True(Blocks(issues, "partition.boot", OperationKind.Delete));
        Assert.True(Blocks(issues, "partition.boot", OperationKind.Format));
        Assert.DoesNotContain(issues, i => i.BlocksOperation(OperationKind.Extend) || i.BlocksOperation(OperationKind.Shrink));
    }

    [Theory]
    [InlineData("page-file")]
    [InlineData("hibernation-file")]
    [InlineData("bitlocker")]
    [InlineData("bitlocker-unknown")]
    [InlineData("shadow-copies")]
    [InlineData("shadow-storage")]
    [InlineData("refs")]
    public void VolumeUsageBlocksMove(string what)
    {
        var v = what switch
        {
            "page-file" => Volume() with { HasPageFile = true },
            "hibernation-file" => Volume() with { HasHibernationFile = true },
            "bitlocker" => Volume() with { BitLocker = BitLockerState.Encrypted },
            "bitlocker-unknown" => Volume() with { BitLocker = BitLockerState.Unknown },
            "shadow-copies" => Volume() with { HasShadowCopies = true },
            "shadow-storage" => Volume() with { HostsShadowStorage = true },
            "refs" => Volume() with { FileSystem = "ReFS" },
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };
        Assert.True(Blocks(Classifier.ForPartition(Data(v)), $"volume.{what}", OperationKind.Move));
    }

    [Fact]
    public void RefsCannotShrinkButCanExtend()
    {
        var issues = Classifier.ForPartition(Data(Volume() with { FileSystem = "ReFS" }));
        Assert.Contains(issues, i => i.BlocksOperation(OperationKind.Shrink));
        Assert.DoesNotContain(issues, i => i.BlocksOperation(OperationKind.Extend));
    }

    [Fact]
    public void PlatformRequiredAttributeBlocksEverything()
    {
        var issues = Classifier.ForPartition(Data() with { GptAttributes = GptAttributes.PlatformRequired });
        Assert.True(Blocks(issues, "partition.platform-required", OperationKind.Extend));
    }

    [Fact]
    public void LogicalDriveCannotMove()
    {
        Assert.True(Blocks(Classifier.ForPartition(Data(Volume()) with { IsLogical = true }), "partition.logical", OperationKind.Move));
    }

    [Theory]
    [InlineData(PartitionRole.DynamicData, "disk.dynamic")]
    [InlineData(PartitionRole.DynamicMetadata, "disk.dynamic")]
    [InlineData(PartitionRole.StorageSpaces, "disk.storage-spaces")]
    [InlineData(PartitionRole.StorageReplica, "disk.storage-replica")]
    public void UnsupportedDiskTypesBlockEverything(PartitionRole role, string code)
    {
        var issues = Classifier.ForDisk(Disk(Data() with { Role = role }));
        Assert.True(Blocks(issues, code, OperationKind.Extend));
        Assert.True(Blocks(issues, code, OperationKind.Create));
    }

    [Fact]
    public void DiskStateFlagsBlockEverything()
    {
        Assert.True(Blocks(Classifier.ForDisk(Disk() with { IsOffline = true }), "disk.offline", OperationKind.Create));
        Assert.True(Blocks(Classifier.ForDisk(Disk() with { IsReadOnly = true }), "disk.read-only", OperationKind.Create));
        Assert.True(Blocks(Classifier.ForDisk(Disk() with { IsClustered = true }), "disk.clustered", OperationKind.Create));
        Assert.True(Blocks(Classifier.ForDisk(Disk() with { BusType = "Storage Spaces" }), "disk.storage-spaces", OperationKind.Create));
    }

    [Fact]
    public void AzureTemporaryDiskIsWarnedNotBlocked()
    {
        var issues = Classifier.ForDisk(Disk() with { IsAzureResourceDisk = true });
        var issue = Assert.Single(issues);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal(OperationKind.None, issue.Blocks);
    }

    [Fact]
    public void MachineIssues()
    {
        Assert.Contains(Classifier.ForMachine(HostEnvironment.Azure, elevated: true), i => i.Code == "azure.snapshot");
        Assert.Contains(Classifier.ForMachine(HostEnvironment.Physical, elevated: false), i => i.Code == "machine.not-elevated");
        Assert.Empty(Classifier.ForMachine(HostEnvironment.HyperV, elevated: true));
    }

    [Fact]
    public void RolesFromPartitionTypes()
    {
        Assert.Equal(PartitionRole.EfiSystem, GptTypes.RoleOf(GptTypes.EfiSystem));
        Assert.Equal(PartitionRole.Unknown, GptTypes.RoleOf(new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4"))); // Linux
        Assert.Equal(PartitionRole.Data, MbrTypes.RoleOf(MbrTypes.Ifs));
        Assert.Equal(PartitionRole.DynamicData, MbrTypes.RoleOf(MbrTypes.Ldm));
        Assert.Equal(PartitionRole.Unknown, MbrTypes.RoleOf(0x83)); // Linux
        Assert.Equal(PartitionRole.MoveInProgress, GptTypes.RoleOf(GptTypes.MoveInProgress));
        Assert.Equal(PartitionRole.MoveInProgress, MbrTypes.RoleOf(MbrTypes.MoveInProgress));
    }
}
