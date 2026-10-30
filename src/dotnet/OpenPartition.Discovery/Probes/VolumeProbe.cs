using System.ComponentModel;
using OpenPartition.Discovery.Native;

namespace OpenPartition.Discovery.Probes;

internal sealed record VolumeFacts
{
    public required string Path { get; init; }
    public IReadOnlyList<(int Disk, long Offset, long Length)> Extents { get; init; } = [];
    public char? DriveLetter { get; init; }
    public IReadOnlyList<string> MountPoints { get; init; } = [];
    public string? FileSystem { get; init; }
    public string? Label { get; init; }
    public long? Capacity { get; init; }
    public long? FreeSpace { get; init; }
}

internal static class VolumeProbe
{
    /// <summary>Normalise a volume GUID path to lower case with a trailing backslash.</summary>
    public static string Normalize(string volumePath) =>
        (volumePath.EndsWith('\\') ? volumePath : volumePath + "\\").ToLowerInvariant();

    public static unsafe IReadOnlyList<VolumeFacts> Enumerate(List<string> notes)
    {
        var names = new List<string>();
        var buffer = stackalloc char[1024];
        var find = NativeMethods.FindFirstVolume(buffer, 1024);
        if (find == new IntPtr(-1))
        {
            notes.Add($"Could not enumerate volumes: {new Win32Exception().Message}");
            return [];
        }
        try
        {
            do
            {
                names.Add(new string(buffer));
            }
            while (NativeMethods.FindNextVolume(find, buffer, 1024));
        }
        finally
        {
            NativeMethods.FindVolumeClose(find);
        }
        return names.Select(n => Describe(n, notes)).ToList();
    }

    private static VolumeFacts Describe(string volumePath, List<string> notes)
    {
        var facts = new VolumeFacts { Path = Normalize(volumePath) };
        try
        {
            using var handle = DeviceHandle.OpenForQuery(volumePath.TrimEnd('\\'));
            facts = facts with { Extents = LayoutParser.ParseVolumeExtents(DeviceHandle.Query(handle, IoctlCodes.VolumeGetVolumeDiskExtents, 256)) };
        }
        catch (Win32Exception e)
        {
            // Volumes on CD-ROMs, RAM disks or not-yet-arrived devices have no disk extents.
            notes.Add($"Volume {volumePath}: no disk extents ({e.Message})");
        }

        var (letter, mounts) = PathNames(volumePath);
        facts = facts with { DriveLetter = letter, MountPoints = mounts };
        var (fileSystem, label) = Information(volumePath);
        facts = facts with { FileSystem = fileSystem, Label = label };
        if (NativeMethods.GetDiskFreeSpaceEx(volumePath, out _, out var total, out var free))
        {
            facts = facts with { Capacity = (long)total, FreeSpace = (long)free };
        }
        return facts;
    }

    private static unsafe (char? Letter, IReadOnlyList<string> MountPoints) PathNames(string volumePath)
    {
        var buffer = new char[4096];
        fixed (char* p = buffer)
        {
            if (!NativeMethods.GetVolumePathNamesForVolumeName(volumePath, p, (uint)buffer.Length, out _))
            {
                return (null, []);
            }
        }
        var paths = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        char? letter = null;
        var mounts = new List<string>();
        foreach (var path in paths)
        {
            if (path.Length == 3 && path[1] == ':' && path[2] == '\\')
            {
                letter ??= char.ToUpperInvariant(path[0]);
            }
            else
            {
                mounts.Add(path);
            }
        }
        return (letter, mounts);
    }

    private static unsafe (string? FileSystem, string? Label) Information(string volumePath)
    {
        var label = stackalloc char[261];
        var fileSystem = stackalloc char[261];
        if (!NativeMethods.GetVolumeInformation(volumePath, label, 261, out _, out _, out _, fileSystem, 261))
        {
            return (null, null);
        }
        var fs = new string(fileSystem);
        var l = new string(label);
        return (fs.Length == 0 ? null : fs, l.Length == 0 ? null : l);
    }

    /// <summary>Volume GUID path of the volume containing <paramref name="path"/>, or null.</summary>
    public static unsafe string? VolumeOf(string path)
    {
        var root = stackalloc char[1024];
        if (!NativeMethods.GetVolumePathName(path, root, 1024))
        {
            return null;
        }
        var guid = stackalloc char[1024];
        return NativeMethods.GetVolumeNameForVolumeMountPoint(new string(root), guid, 1024) ? Normalize(new string(guid)) : null;
    }
}
