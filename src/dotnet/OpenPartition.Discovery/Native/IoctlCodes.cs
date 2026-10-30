namespace OpenPartition.Discovery.Native;

/// <summary>
/// Every IOCTL discovery may send. All of them are FILE_ANY_ACCESS or FILE_READ_ACCESS;
/// <c>ReadOnlyEnforcementTests</c> decodes each constant and fails if one needs write access.
/// </summary>
internal static class IoctlCodes
{
    public const uint DiskGetDriveGeometryEx = 0x0007_00A0;
    public const uint DiskGetDriveLayoutEx = 0x0007_0050;
    public const uint DiskGetLengthInfo = 0x0007_405C;
    public const uint VolumeGetVolumeDiskExtents = 0x0056_0000;
    public const uint StorageGetDeviceNumber = 0x002D_1080;

    /// <summary>Required access encoded in bits 14-15 of an IOCTL code: 0 any, 1 read, 2 write, 3 both.</summary>
    public static uint RequiredAccess(uint code) => (code >> 14) & 0x3;
}
