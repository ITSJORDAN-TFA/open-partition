using System.Buffers.Binary;
using System.Text;
using OpenPartition.Model;

namespace OpenPartition.Discovery.Native;

/// <summary>A partition table entry exactly as IOCTL_DISK_GET_DRIVE_LAYOUT_EX reports it.</summary>
internal sealed record RawPartition(
    int Number,
    long Offset,
    long Length,
    byte? MbrType,
    bool BootIndicator,
    Guid? GptType,
    Guid? PartitionId,
    ulong GptAttributes,
    string? GptName);

internal sealed record RawLayout(
    PartitionStyle Style,
    uint? MbrSignature,
    Guid? GptDiskId,
    long? GptStartingUsableOffset,
    long? GptUsableLength,
    IReadOnlyList<RawPartition> Partitions);

/// <summary>
/// Parses the output of IOCTL_DISK_GET_DRIVE_LAYOUT_EX and IOCTL_DISK_GET_DRIVE_GEOMETRY_EX
/// by byte offset (x64 and x86 layouts are identical for these structures), so no structs
/// are marshalled and every field read is bounds-checked.
/// </summary>
internal static class LayoutParser
{
    // DRIVE_LAYOUT_INFORMATION_EX: style(4) count(4) union(40) then PARTITION_INFORMATION_EX[].
    private const int LayoutHeaderSize = 48;
    // PARTITION_INFORMATION_EX is 144 bytes: style(4) pad(4) offset(8) length(8) number(4)
    // rewrite(1) service(1) pad(2), then a 112-byte MBR/GPT union at 32.
    internal const int PartitionEntrySize = 144;

    public static RawLayout ParseLayout(ReadOnlySpan<byte> data)
    {
        if (data.Length < LayoutHeaderSize)
        {
            throw new FormatException("Drive layout buffer is too small.");
        }
        var style = BinaryPrimitives.ReadUInt32LittleEndian(data) switch
        {
            0 => PartitionStyle.Mbr,
            1 => PartitionStyle.Gpt,
            2 => PartitionStyle.Raw,
            _ => PartitionStyle.Unknown,
        };
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (count > 1024 || LayoutHeaderSize + (count * PartitionEntrySize) > data.Length)
        {
            throw new FormatException($"Drive layout claims {count} partitions but the buffer is {data.Length} bytes.");
        }

        uint? signature = null;
        Guid? diskId = null;
        long? usableStart = null, usableLength = null;
        if (style == PartitionStyle.Mbr)
        {
            signature = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        }
        else if (style == PartitionStyle.Gpt)
        {
            diskId = new Guid(data.Slice(8, 16));
            usableStart = BinaryPrimitives.ReadInt64LittleEndian(data[24..]);
            usableLength = BinaryPrimitives.ReadInt64LittleEndian(data[32..]);
        }

        var partitions = new List<RawPartition>();
        for (var i = 0; i < count; i++)
        {
            var entry = data.Slice(LayoutHeaderSize + (i * PartitionEntrySize), PartitionEntrySize);
            var offset = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
            var length = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
            var number = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);
            var union = entry[32..];

            if (style == PartitionStyle.Mbr)
            {
                var type = union[0];
                // MBR layouts are padded to multiples of 4 with empty entries.
                if (type == 0 || length == 0)
                {
                    continue;
                }
                // Entries 0-3 are the primary table. After that the layout lists each EBR in
                // the logical-drive chain as a group of 4: the logical drive plus an extended
                // entry linking to the next EBR. Those links are not partitions (Windows does
                // not list them either); the primary extended entry is the container.
                if (i >= 4 && MbrTypes.IsExtended(type))
                {
                    continue;
                }
                partitions.Add(new RawPartition(number, offset, length, type, union[1] != 0, null, null, 0, null));
            }
            else if (style == PartitionStyle.Gpt)
            {
                var type = new Guid(union[..16]);
                if (type == Guid.Empty)
                {
                    continue;
                }
                var name = Encoding.Unicode.GetString(union.Slice(40, 72)).TrimEnd('\0');
                partitions.Add(new RawPartition(
                    number, offset, length, null, false, type, new Guid(union.Slice(16, 16)),
                    BinaryPrimitives.ReadUInt64LittleEndian(union[32..]), name));
            }
        }
        return new RawLayout(style, signature, diskId, usableStart, usableLength, partitions);
    }

    /// <summary>Returns (bytes per sector, disk size) from DISK_GEOMETRY_EX.</summary>
    public static (int BytesPerSector, long DiskSize) ParseGeometry(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
        {
            throw new FormatException("Drive geometry buffer is too small.");
        }
        return ((int)BinaryPrimitives.ReadUInt32LittleEndian(data[20..]), BinaryPrimitives.ReadInt64LittleEndian(data[24..]));
    }

    /// <summary>Returns the (disk number, offset, length) extents of a volume from VOLUME_DISK_EXTENTS.</summary>
    public static IReadOnlyList<(int Disk, long Offset, long Length)> ParseVolumeExtents(ReadOnlySpan<byte> data)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > 4096 || 8 + (count * 24) > data.Length)
        {
            throw new FormatException("Volume extents buffer is inconsistent.");
        }
        var extents = new List<(int, long, long)>((int)count);
        for (var i = 0; i < count; i++)
        {
            var e = data.Slice(8 + (i * 24), 24);
            extents.Add(((int)BinaryPrimitives.ReadUInt32LittleEndian(e),
                BinaryPrimitives.ReadInt64LittleEndian(e[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(e[16..])));
        }
        return extents;
    }
}
