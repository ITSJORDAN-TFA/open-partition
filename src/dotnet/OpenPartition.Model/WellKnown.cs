namespace OpenPartition.Model;

public static class GptTypes
{
    public static readonly Guid BasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid MicrosoftReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    public static readonly Guid Recovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
    public static readonly Guid LdmMetadata = new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3");
    public static readonly Guid LdmData = new("AF9B60A0-1431-4F62-BC68-3311714A69AD");
    public static readonly Guid StorageSpaces = new("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D");
    public static readonly Guid StorageReplica = new("558D43C5-A1AC-43C0-AAC8-D1472B2923D1");
    /// <summary>Open Partition Manager's own "move in progress" type: hides a partition mid-move.</summary>
    public static readonly Guid MoveInProgress = new("5F0C7D62-6B3C-4C2B-9A1E-0F504D4D4F56");

    public static PartitionRole RoleOf(Guid type) => type switch
    {
        _ when type == BasicData => PartitionRole.Data,
        _ when type == EfiSystem => PartitionRole.EfiSystem,
        _ when type == MicrosoftReserved => PartitionRole.MicrosoftReserved,
        _ when type == Recovery => PartitionRole.Recovery,
        _ when type == LdmMetadata => PartitionRole.DynamicMetadata,
        _ when type == LdmData => PartitionRole.DynamicData,
        _ when type == StorageSpaces => PartitionRole.StorageSpaces,
        _ when type == StorageReplica => PartitionRole.StorageReplica,
        _ when type == MoveInProgress => PartitionRole.MoveInProgress,
        _ => PartitionRole.Unknown,
    };
}

public static class GptAttributes
{
    public const ulong PlatformRequired = 0x0000_0000_0000_0001;
    public const ulong ReadOnly = 0x1000_0000_0000_0000;
    public const ulong ShadowCopy = 0x2000_0000_0000_0000;
    public const ulong Hidden = 0x4000_0000_0000_0000;
    public const ulong NoDriveLetter = 0x8000_0000_0000_0000;
}

public static class MbrTypes
{
    public const byte Fat12 = 0x01;
    public const byte Fat16Small = 0x04;
    public const byte Extended = 0x05;
    public const byte Fat16 = 0x06;
    public const byte Ifs = 0x07;
    public const byte Fat32 = 0x0B;
    public const byte Fat32Lba = 0x0C;
    public const byte Fat16Lba = 0x0E;
    public const byte ExtendedLba = 0x0F;
    public const byte WindowsRecovery = 0x27;
    public const byte Ldm = 0x42;
    public const byte GptProtective = 0xEE;
    public const byte EfiSystem = 0xEF;
    /// <summary>Open Partition Manager's own "move in progress" type.</summary>
    public const byte MoveInProgress = 0x7F;

    public static bool IsExtended(byte type) => type is Extended or ExtendedLba;

    public static PartitionRole RoleOf(byte type) => type switch
    {
        Fat12 or Fat16Small or Fat16 or Ifs or Fat32 or Fat32Lba or Fat16Lba => PartitionRole.Data,
        Extended or ExtendedLba => PartitionRole.Extended,
        WindowsRecovery => PartitionRole.Recovery,
        Ldm => PartitionRole.DynamicData,
        EfiSystem => PartitionRole.EfiSystem,
        MoveInProgress => PartitionRole.MoveInProgress,
        _ => PartitionRole.Unknown,
    };
}

public static class Units
{
    public const long MiB = 1024 * 1024;

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.0} {units[unit]}";
    }
}
