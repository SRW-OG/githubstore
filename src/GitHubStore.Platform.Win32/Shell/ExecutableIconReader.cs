using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace GitHubStore.Platform.Win32.Shell;

/// <summary>Reads native icon resources as PE data. Never loads or executes the target module.</summary>
public static class ExecutableIconReader
{
    private const long MaximumFileBytes = 512L * 1024 * 1024;
    private const int MaximumResourceBytes = 32 * 1024 * 1024;
    private const int MaximumFrameBytes = 8 * 1024 * 1024;
    private const int MaximumFrames = 64;

    public static byte[]? ReadIcon(string executablePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)) return null;
            using var file = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is < 64 or > MaximumFileBytes) return null;
            using var pe = new PEReader(file);
            var directory = pe.PEHeaders.PEHeader?.ResourceTableDirectory;
            if (directory is not { RelativeVirtualAddress: > 0, Size: >= 16 } resource || resource.Size > MaximumResourceBytes) return null;
            var section = pe.GetSectionData(resource.RelativeVirtualAddress);
            if (section.Length < resource.Size) return null;
            var bytes = section.GetContent(0, resource.Size).ToArray();
            return new ResourceReader(pe, bytes).Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
            or ArgumentException or InvalidOperationException or OverflowException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class ResourceReader(PEReader pe, byte[] bytes)
    {
        private int _entryBudget = 10000;
        private sealed record Entry(uint? Id, string? Name, int Offset, bool Directory);

        internal byte[]? Read()
        {
            var root = Entries(0);
            var groups = root.FirstOrDefault(entry => entry.Id == 14 && entry.Directory);
            var icons = root.FirstOrDefault(entry => entry.Id == 3 && entry.Directory);
            if (groups is null || icons is null) return null;
            var iconEntries = Entries(icons.Offset);
            foreach (var group in Entries(groups.Offset).Where(entry => entry.Directory).OrderBy(Priority))
            {
                foreach (var language in Entries(group.Offset).Where(entry => !entry.Directory).OrderBy(entry => entry.Id == 0 ? 0 : 1))
                {
                    var groupBytes = Data(language.Offset, 6 + 14 * MaximumFrames);
                    var result = Reconstruct(groupBytes, language.Id, iconEntries);
                    if (result is not null) return result;
                }
            }
            return null;
        }

        private static int Priority(Entry entry) => entry.Name?.ToUpperInvariant() switch
        {
            "IDI_ICON1" => 0,
            "MAINICON" => 1,
            "APPICON" => 2,
            "IDI_KDBX" => 4,
            _ => 3,
        };

        private byte[]? Reconstruct(byte[] group, uint? language, IReadOnlyList<Entry> iconEntries)
        {
            if (group.Length < 6 || U16(group, 0) != 0 || U16(group, 2) != 1) return null;
            var count = U16(group, 4);
            if (count is 0 or > MaximumFrames || group.Length != 6 + count * 14) return null;
            var frames = new List<byte[]>(count);
            var total = 6 + count * 16;
            for (var i = 0; i < count; i++)
            {
                var position = 6 + i * 14;
                var id = U16(group, position + 12);
                var icon = iconEntries.FirstOrDefault(entry => entry.Id == id && entry.Directory);
                if (icon is null) return null;
                var leaf = Entries(icon.Offset).Where(entry => !entry.Directory)
                    .OrderBy(entry => entry.Id == language ? 0 : entry.Id == 0 ? 1 : 2).FirstOrDefault();
                if (leaf is null) return null;
                var image = Data(leaf.Offset, MaximumFrameBytes);
                if (image.Length == 0 || U32(group, position + 8) != image.Length) return null;
                total = checked(total + image.Length);
                if (total > MaximumResourceBytes) return null;
                frames.Add(image);
            }
            // Emit one frame so decoders cannot select the first, smallest image.
            var best = Enumerable.Range(0, count)
                .OrderByDescending(index => Dimension(group[6 + index * 14]) * Dimension(group[7 + index * 14]))
                .ThenByDescending(index => U16(group, 12 + index * 14))
                .ThenByDescending(index => frames[index].Length).First();
            var output = new byte[22 + frames[best].Length];
            group.AsSpan(0, 6).CopyTo(output);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(4, 2), 1);
            group.AsSpan(6 + best * 14, 12).CopyTo(output.AsSpan(6));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(18, 4), 22);
            frames[best].CopyTo(output, 22);
            return output;
        }

        private static int Dimension(byte value) => value == 0 ? 256 : value;

        private List<Entry> Entries(int offset)
        {
            Require(offset, 16);
            var count = checked(U16(bytes, offset + 12) + U16(bytes, offset + 14));
            if (count > 4096 || (_entryBudget -= count) < 0) throw new BadImageFormatException();
            Require(checked(offset + 16), checked(count * 8));
            var entries = new List<Entry>(count);
            for (var i = 0; i < count; i++)
            {
                var name = U32(bytes, offset + 16 + i * 8);
                var target = U32(bytes, offset + 20 + i * 8);
                string? text = null;
                uint? id = name;
                if ((name & 0x80000000) != 0)
                {
                    id = null;
                    var nameOffset = checked((int)(name & 0x7fffffff));
                    Require(nameOffset, 2);
                    var length = U16(bytes, nameOffset);
                    if (length > 256) throw new BadImageFormatException();
                    Require(checked(nameOffset + 2), length * 2);
                    text = Encoding.Unicode.GetString(bytes, nameOffset + 2, length * 2);
                }
                var child = checked((int)(target & 0x7fffffff));
                Require(child, 16);
                entries.Add(new(id, text, child, (target & 0x80000000) != 0));
            }
            return entries;
        }

        private byte[] Data(int offset, int maximum)
        {
            Require(offset, 16);
            var rva = U32(bytes, offset);
            var size = U32(bytes, offset + 4);
            if (rva > int.MaxValue || size > maximum) throw new BadImageFormatException();
            var data = pe.GetSectionData((int)rva);
            if (data.Length < size) throw new BadImageFormatException();
            return data.GetContent(0, (int)size).ToArray();
        }

        private void Require(int offset, int length)
        {
            if (offset < 0 || length < 0 || offset > bytes.Length - length) throw new BadImageFormatException();
        }
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
}
