using System.Buffers.Binary;
using System.Text;
using OpenPartition.Discovery.Native;
using OpenPartition.Model;

namespace OpenPartition.Tests;

/// <summary>Builds DRIVE_LAYOUT_INFORMATION_EX buffers byte by byte, as the IOCTL returns them.</summary>
public class LayoutParserTests
{
    private const int Header = 48;
    private const int Entry = LayoutParser.PartitionEntrySize;

    private static byte[] Layout(int style, int count, Action<Span<byte>> header, params Action<Span<byte>>[] entries)
    {
        var buffer = new byte[Header + (count * Entry)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)style);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)count);
        header(buffer.AsSpan(8, 40));
        for (var i = 0; i < entries.Length; i++)
        {
            var e = buffer.AsSpan(Header + (i * Entry), Entry);
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)style);
            entries[i](e);
        }
        return buffer;
    }

    private static void Common(Span<byte> e, long offset, long length, int number)
    {
        BinaryPrimitives.WriteInt64LittleEndian(e[8..], offset);
        BinaryPrimitives.WriteInt64LittleEndian(e[16..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(e[24..], (uint)number);
    }

    [Fact]
    public void ParsesGptLayout()
    {
        var diskId = Guid.NewGuid();
        var partId = Guid.NewGuid();
        var data = Layout(1, 2,
            h =>
            {
                diskId.TryWriteBytes(h);
                BinaryPrimitives.WriteInt64LittleEndian(h[16..], 17408);
                BinaryPrimitives.WriteInt64LittleEndian(h[24..], 1_000_000_000);
            },
            e =>
            {
                Common(e, Units.MiB, 512 * Units.MiB, 1);
                GptTypes.BasicData.TryWriteBytes(e[32..]);
                partId.TryWriteBytes(e[48..]);
                BinaryPrimitives.WriteUInt64LittleEndian(e[64..], GptAttributes.NoDriveLetter);
                Encoding.Unicode.GetBytes("Basic data partition").CopyTo(e[72..]);
            });

        var layout = LayoutParser.ParseLayout(data);

        Assert.Equal(PartitionStyle.Gpt, layout.Style);
        Assert.Equal(diskId, layout.GptDiskId);
        Assert.Equal(17408, layout.GptStartingUsableOffset);
        Assert.Equal(1_000_000_000, layout.GptUsableLength);
        var p = Assert.Single(layout.Partitions); // the zero-type second entry is skipped
        Assert.Equal(1, p.Number);
        Assert.Equal(Units.MiB, p.Offset);
        Assert.Equal(512 * Units.MiB, p.Length);
        Assert.Equal(GptTypes.BasicData, p.GptType);
        Assert.Equal(partId, p.PartitionId);
        Assert.Equal(GptAttributes.NoDriveLetter, p.GptAttributes);
        Assert.Equal("Basic data partition", p.GptName);
    }

    [Fact]
    public void ParsesMbrLayoutAndSkipsEmptySlots()
    {
        var data = Layout(0, 4,
            h => BinaryPrimitives.WriteUInt32LittleEndian(h, 0xDEADBEEF),
            e =>
            {
                Common(e, Units.MiB, 100 * Units.MiB, 1);
                e[32] = MbrTypes.Ifs;
                e[33] = 1;
            },
            e =>
            {
                Common(e, 101 * Units.MiB, 200 * Units.MiB, 0);
                e[32] = MbrTypes.ExtendedLba;
            });

        var layout = LayoutParser.ParseLayout(data);

        Assert.Equal(PartitionStyle.Mbr, layout.Style);
        Assert.Equal(0xDEADBEEFu, layout.MbrSignature);
        Assert.Equal(2, layout.Partitions.Count);
        Assert.True(layout.Partitions[0].BootIndicator);
        Assert.Equal(MbrTypes.Ifs, layout.Partitions[0].MbrType);
        Assert.Equal(MbrTypes.ExtendedLba, layout.Partitions[1].MbrType);
    }

    [Fact]
    public void MbrLogicalDriveChainLinksAreNotPartitions()
    {
        // Shape seen on a real Azure Gen 1 VM: 3 primaries + extended container, then one
        // EBR group per logical drive, each with a link entry to the next EBR.
        static void Entry(Span<byte> e, long offsetMiB, long sizeMiB, int number, byte type)
        {
            Common(e, offsetMiB * Units.MiB, sizeMiB * Units.MiB, number);
            e[32] = type;
        }
        var data = Layout(0, 12,
            h => BinaryPrimitives.WriteUInt32LittleEndian(h, 0xCF892F14),
            e => Entry(e, 1, 500, 1, MbrTypes.Ifs),
            e => Entry(e, 501, 1000, 2, MbrTypes.Ifs),
            e => Entry(e, 1501, 500, 3, MbrTypes.Ifs),
            e => Entry(e, 2001, 3000, 0, MbrTypes.ExtendedLba),     // container
            e => Entry(e, 2002, 999, 4, MbrTypes.Ifs),              // logical E:
            e => Entry(e, 3001, 1000, 0, MbrTypes.Extended),        // link to next EBR
            _ => { },
            _ => { },
            e => Entry(e, 3002, 999, 5, MbrTypes.Ifs),              // logical L:
            e => Entry(e, 4001, 1000, 0, MbrTypes.Extended),        // link
            _ => { },
            _ => { });

        var layout = LayoutParser.ParseLayout(data);

        Assert.Equal([1, 2, 3, 0, 4, 5], layout.Partitions.Select(p => p.Number));
        Assert.Single(layout.Partitions, p => MbrTypes.IsExtended(p.MbrType!.Value));
    }

    [Fact]
    public void RejectsTruncatedBuffer()
    {
        var data = Layout(1, 3, _ => { });
        Assert.Throws<FormatException>(() => LayoutParser.ParseLayout(data.AsSpan(0, data.Length - 1)));
        Assert.Throws<FormatException>(() => LayoutParser.ParseLayout(new byte[10]));
    }

    [Fact]
    public void ParsesGeometryAndExtents()
    {
        var geometry = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(20), 4096);
        BinaryPrimitives.WriteInt64LittleEndian(geometry.AsSpan(24), 4L << 30);
        Assert.Equal((4096, 4L << 30), LayoutParser.ParseGeometry(geometry));

        var extents = new byte[8 + 48];
        BinaryPrimitives.WriteUInt32LittleEndian(extents, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(extents.AsSpan(8), 3);
        BinaryPrimitives.WriteInt64LittleEndian(extents.AsSpan(16), Units.MiB);
        BinaryPrimitives.WriteInt64LittleEndian(extents.AsSpan(24), 5 * Units.MiB);
        BinaryPrimitives.WriteUInt32LittleEndian(extents.AsSpan(32), 4);
        var parsed = LayoutParser.ParseVolumeExtents(extents);
        Assert.Equal(2, parsed.Count);
        Assert.Equal((3, Units.MiB, 5 * Units.MiB), parsed[0]);
        Assert.Equal(4, parsed[1].Disk);
    }
}
