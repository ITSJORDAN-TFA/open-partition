namespace OpenPartition.Model;

[Flags]
public enum OperationKind
{
    None = 0,
    Extend = 1 << 0,
    Shrink = 1 << 1,
    Move = 1 << 2,
    Create = 1 << 3,
    Delete = 1 << 4,
    Format = 1 << 5,
    ChangeDriveLetter = 1 << 6,
    All = Extend | Shrink | Move | Create | Delete | Format | ChangeDriveLetter,
}

public enum IssueSeverity
{
    Info,
    Warning,
    /// <summary>Stops every operation listed in <see cref="Issue.Blocks"/>.</summary>
    Blocker,
}

/// <summary>
/// Something the tool must refuse or warn about. Every operation is checked against the
/// issues of its disk and partition; an operation is allowed only if no issue blocks it.
/// </summary>
public sealed record Issue(string Code, IssueSeverity Severity, string Message, OperationKind Blocks = OperationKind.None)
{
    public bool BlocksOperation(OperationKind operation) => (Blocks & operation) != 0;
}

public static class IssueExtensions
{
    /// <summary>The issues that stop <paramref name="operation"/> on this partition, including disk-level ones.</summary>
    public static IEnumerable<Issue> BlockersFor(this DiskInfo disk, PartitionInfo partition, OperationKind operation) =>
        disk.Issues.Concat(partition.Issues).Where(i => i.BlocksOperation(operation));

    public static bool Allows(this DiskInfo disk, PartitionInfo partition, OperationKind operation) =>
        !disk.BlockersFor(partition, operation).Any();
}
