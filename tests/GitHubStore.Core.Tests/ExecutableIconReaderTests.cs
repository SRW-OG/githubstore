using System.Buffers.Binary;
using System.Text;
using GitHubStore.Platform.Win32.Shell;
using Xunit;

namespace GitHubStore.Core.Tests;

public sealed class ExecutableIconReaderTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6tAAAAABJRU5ErkJggg==");

    [Theory]
    [InlineData(null, 0)]
    [InlineData("IDI_ICON1", 1033)]
    [InlineData("MAINICON", 2052)]
    public void NumericAndNamedGroupsAndLanguages_PreservePngFrame(string? name, int language)
    {
        var pe = Fixture([new Group(name, [new Frame(0, 32, Png)])], language);
        var icon = Read(pe);
        Assert.NotNull(icon);
        Assert.Equal(1, U16(icon!, 2));
        Assert.Equal(1, U16(icon!, 4));
        Assert.Equal(22u, U32(icon!, 18));
        Assert.Equal((uint)Png.Length, U32(icon!, 14));
        Assert.Equal(Png, icon![22..]);
    }

    [Fact]
    public void BestFrame_PrefersResolutionThenBitDepthThenPayloadLength()
    {
        var winning = Png.Concat(new byte[] { 1, 2, 3 }).ToArray();
        var pe = Fixture([new Group(null,
        [new Frame(16, 32, new byte[200]), new Frame(0, 8, new byte[500]),
         new Frame(0, 32, Png), new Frame(0, 32, winning)])]);
        var icon = Read(pe)!;
        Assert.Equal(1, U16(icon, 4));
        Assert.Equal(0, icon[6]);
        Assert.Equal(32, U16(icon, 12));
        Assert.Equal(winning, icon[22..]);
    }

    [Theory]
    [InlineData("IDI_ICON1")]
    [InlineData("MAINICON")]
    [InlineData("APPICON")]
    public void AppGroupNames_ArePreferredOverDocumentIcon(string preferred)
    {
        var pe = Fixture([
            new Group("IDI_KDBX", [new Frame(0, 32, new byte[] { 9 })]),
            new Group(preferred, [new Frame(0, 32, Png)])]);
        Assert.Equal(Png, Read(pe)![22..]);
    }

    [Fact]
    public void MissingResourcesAndMalformedOffsets_ReturnNull()
    {
        Assert.Null(Read(new byte[100]));
        var pe = Fixture([new Group(null, [new Frame(0, 32, Png)])]);
        var noResources = pe.ToArray();
        Put32(noResources, 0x80 + 24 + 112 + 16, 0);
        Put32(noResources, 0x80 + 24 + 112 + 20, 0);
        Assert.Null(Read(noResources));
        var invalidDirectory = pe.ToArray();
        Put32(invalidDirectory, 0x200 + 20, 0xfffffff0);
        Assert.Null(Read(invalidDirectory));
        var excessiveCount = pe.ToArray();
        Put16(excessiveCount, 0x200 + 14, ushort.MaxValue);
        Assert.Null(Read(excessiveCount));
        Assert.Null(Read(pe[..0x210]));
    }

    [Fact]
    public void InvalidFrameSizeOrMissingFrame_RejectsEntireGroup()
    {
        Assert.Null(Read(Fixture([new Group(null, [new Frame(0, 32, Png, DeclaredSize: 999)])])));
        Assert.Null(Read(Fixture([new Group(null, [new Frame(0, 32, Png, ReferencedId: 999)])])));
        Assert.Null(Read(Fixture([new Group(null, [new Frame(0, 32, Png, DataSize: 9 * 1024 * 1024)])])));
        Assert.Null(Read(Fixture([new Group(null, [])])));
        Assert.Null(Read(Fixture([new Group(null, Enumerable.Range(0, 65).Select(_ => new Frame(16, 32, Png)).ToArray())])));
    }

    [Fact]
    public void MissingAndRelativePaths_ReturnNull()
    {
        Assert.Null(ExecutableIconReader.ReadIcon("relative.exe"));
        Assert.Null(ExecutableIconReader.ReadIcon(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
    }

    private static byte[]? Read(byte[] pe)
    {
        var directory = Path.Combine(Path.GetTempPath(), "githubstore-icon-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fixture.exe");
        try { File.WriteAllBytes(path, pe); return ExecutableIconReader.ReadIcon(path); }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    private sealed record Frame(byte Dimension, ushort Bits, byte[] Bytes, uint? DeclaredSize = null, ushort? ReferencedId = null, uint? DataSize = null);
    private sealed record Group(string? Name, Frame[] Frames);
    private sealed record Node(uint? Id, string? Name, Node[]? Children = null, byte[]? Data = null, uint? DataSize = null);

    /// <summary>Minimal PE32+ file with an ordinary resource directory, built without SDK/compiler dependencies.</summary>
    private static byte[] Fixture(Group[] groups, int language = 0)
    {
        var icons = new List<Node>();
        var groupNodes = new List<Node>();
        ushort nextId = 1;
        foreach (var group in groups)
        {
            var data = new byte[6 + group.Frames.Length * 14];
            Put16(data, 2, 1);
            Put16(data, 4, (ushort)group.Frames.Length);
            for (var i = 0; i < group.Frames.Length; i++)
            {
                var frame = group.Frames[i];
                var offset = 6 + i * 14;
                data[offset] = frame.Dimension;
                data[offset + 1] = frame.Dimension;
                Put16(data, offset + 4, 1);
                Put16(data, offset + 6, frame.Bits);
                Put32(data, offset + 8, frame.DeclaredSize ?? (uint)frame.Bytes.Length);
                Put16(data, offset + 12, frame.ReferencedId ?? nextId);
                icons.Add(new(nextId++, null, [new((uint)language, null, Data: frame.Bytes, DataSize: frame.DataSize)]));
            }
            groupNodes.Add(new(group.Name is null ? (uint)(32512 + groupNodes.Count) : null, group.Name,
                [new((uint)language, null, Data: data)]));
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        int Allocate(int size)
        {
            var start = (int)((stream.Length + 3) & ~3L);
            stream.SetLength(start + size);
            return start;
        }
        void At32(int position, uint value) { stream.Position = position; writer.Write(value); }
        int DirectoryBytes(Node[] entries)
        {
            entries = entries.OrderBy(entry => entry.Name is null ? 1 : 0).ToArray();
            var offset = Allocate(16 + entries.Length * 8);
            stream.Position = offset + 12;
            writer.Write((ushort)entries.Count(entry => entry.Name is not null));
            writer.Write((ushort)entries.Count(entry => entry.Name is null));
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                uint key = entry.Id ?? 0;
                if (entry.Name is { } name)
                {
                    var nameOffset = Allocate(2 + name.Length * 2);
                    stream.Position = nameOffset;
                    writer.Write((ushort)name.Length);
                    writer.Write(Encoding.Unicode.GetBytes(name));
                    key = (uint)nameOffset | 0x80000000;
                }
                At32(offset + 16 + i * 8, key);
                if (entry.Children is { } children)
                    At32(offset + 20 + i * 8, (uint)DirectoryBytes(children) | 0x80000000);
                else
                {
                    var descriptor = Allocate(16);
                    var payload = Allocate(entry.Data!.Length);
                    stream.Position = payload;
                    writer.Write(entry.Data);
                    At32(descriptor, 0x1000u + (uint)payload);
                    At32(descriptor + 4, entry.DataSize ?? (uint)entry.Data.Length);
                    At32(offset + 20 + i * 8, (uint)descriptor);
                }
            }
            return offset;
        }
        DirectoryBytes([new(3, null, icons.ToArray()), new(14, null, groupNodes.ToArray())]);
        var resource = stream.ToArray();
        var rawSize = (resource.Length + 511) & ~511;
        var pe = new byte[0x200 + rawSize];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z';
        Put32(pe, 60, 0x80);
        Put32(pe, 0x80, 0x00004550);
        Put16(pe, 0x84, 0x8664);
        Put16(pe, 0x86, 1);
        Put16(pe, 0x94, 240);
        Put16(pe, 0x96, 2);
        const int optional = 0x98;
        Put16(pe, optional, 0x20b);
        Put32(pe, optional + 32, 0x1000);
        Put32(pe, optional + 36, 0x200);
        Put32(pe, optional + 56, (uint)((0x1000 + resource.Length + 4095) & ~4095));
        Put32(pe, optional + 60, 0x200);
        Put16(pe, optional + 68, 3);
        Put32(pe, optional + 108, 16);
        Put32(pe, optional + 112 + 16, 0x1000);
        Put32(pe, optional + 112 + 20, (uint)resource.Length);
        const int section = optional + 240;
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(pe, section);
        Put32(pe, section + 8, (uint)resource.Length);
        Put32(pe, section + 12, 0x1000);
        Put32(pe, section + 16, (uint)rawSize);
        Put32(pe, section + 20, 0x200);
        Put32(pe, section + 36, 0x40000040);
        resource.CopyTo(pe, 0x200);
        return pe;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}
