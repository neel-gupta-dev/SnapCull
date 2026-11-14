using System.Buffers.Binary;
using System.Text;

namespace SnapCull.Core;

/// <summary>Reader for ISO base media file format boxes — the container used by Canon CR3, HEIF/HEIC and AVIF.</summary>
internal static class Bmff
{
    public readonly record struct Box(string Type, long ContentStart, long End)
    {
        public long Length => End - ContentStart;
    }

    public static IEnumerable<Box> Boxes(Stream s, long start, long end)
    {
        long pos = start;
        while (pos + 8 <= end)
        {
            var h = Io.Read(s, pos, end - pos >= 16 ? 16 : 8);
            if (h is null) yield break;

            ulong size = BinaryPrimitives.ReadUInt32BigEndian(h);
            string type = Encoding.ASCII.GetString(h, 4, 4);
            long header = 8;
            if (size == 1)
            {
                if (h.Length < 16) yield break;
                size = BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(8));
                header = 16;
            }
            else if (size == 0)
            {
                size = (ulong)(end - pos);
            }

            if (size < (ulong)header || size > (ulong)(end - pos)) yield break;
            yield return new Box(type, pos + header, pos + (long)size);
            pos += (long)size;
        }
    }

    public static bool HasUuid(Stream s, Box box, byte[] uuid) =>
        box.Length >= 16
        && Io.Read(s, box.ContentStart, 16) is { } id
        && id.AsSpan().SequenceEqual(uuid);

    public static Box? FindPath(Stream s, Box parent, params string[] path)
    {
        var current = parent;
        foreach (var type in path)
        {
            Box? next = null;
            foreach (var b in Boxes(s, current.ContentStart, current.End))
            {
                if (b.Type == type) { next = b; break; }
            }
            if (next is null) return null;
            current = next.Value;
        }
        return current;
    }

    /// <summary>
    /// Finds the EXIF block of a HEIF/HEIC/AVIF file: the "Exif" item listed in meta/iinf, located via meta/iloc.
    /// Returns the absolute position of the TIFF header and the remaining length.
    /// </summary>
    public static ByteRange? FindHeifExif(Stream s)
    {
        Box? meta = null;
        foreach (var b in Boxes(s, 0, s.Length))
            if (b.Type == "meta") { meta = b; break; }
        if (meta is not { } m) return null;

        // meta is a "full box": 4 bytes of version/flags before its children.
        uint? exifItem = null;
        Box? iloc = null;
        foreach (var b in Boxes(s, m.ContentStart + 4, m.End))
        {
            if (b.Type == "iinf") exifItem = FindItemOfType(s, b, "Exif");
            else if (b.Type == "iloc") iloc = b;
        }
        if (exifItem is not { } id || iloc is not { } loc || loc.Length > 1 << 20) return null;
        if (FindItemLocation(Io.Read(s, loc.ContentStart, (int)loc.Length)!, id) is not { } range) return null;

        // The Exif item starts with a 4-byte offset to the TIFF header (it skips the "Exif\0\0" prefix).
        var prefix = Io.Read(s, range.Offset, 4);
        if (prefix is null) return null;
        long tiffStart = range.Offset + 4 + BinaryPrimitives.ReadUInt32BigEndian(prefix);
        long length = range.Offset + range.Length - tiffStart;
        return length > 8 ? new ByteRange(tiffStart, length) : null;
    }

    static uint? FindItemOfType(Stream s, Box iinf, string itemType)
    {
        var header = Io.Read(s, iinf.ContentStart, 4);
        if (header is null) return null;
        long childrenStart = iinf.ContentStart + 4 + (header[0] == 0 ? 2 : 4);

        foreach (var infe in Boxes(s, childrenStart, iinf.End))
        {
            if (infe.Type != "infe" || infe.Length < 12) continue;
            var c = Io.Read(s, infe.ContentStart, (int)Math.Min(infe.Length, 64))!;
            int version = c[0];
            if (version < 2) continue;
            uint id = version == 2 ? BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(4));
            int typeAt = version == 2 ? 8 : 10;
            if (c.Length >= typeAt + 4 && Encoding.ASCII.GetString(c, typeAt, 4) == itemType) return id;
        }
        return null;
    }

    static ByteRange? FindItemLocation(byte[] c, uint wantedId)
    {
        try
        {
            int version = c[0], p = 4;
            int offsetSize = c[p] >> 4, lengthSize = c[p] & 0xF;
            int baseOffsetSize = c[p + 1] >> 4;
            int indexSize = version is 1 or 2 ? c[p + 1] & 0xF : 0;
            p += 2;
            uint count = version < 2 ? Read(c, ref p, 2) : Read(c, ref p, 4);

            for (uint i = 0; i < count; i++)
            {
                uint id = version < 2 ? Read(c, ref p, 2) : Read(c, ref p, 4);
                int construction = version is 1 or 2 ? (int)(Read(c, ref p, 2) & 0xF) : 0;
                Read(c, ref p, 2); // data reference index
                ulong baseOffset = Read(c, ref p, baseOffsetSize);
                uint extents = Read(c, ref p, 2);
                for (uint e = 0; e < extents; e++)
                {
                    if (indexSize > 0) Read(c, ref p, indexSize);
                    ulong offset = Read(c, ref p, offsetSize);
                    ulong length = Read(c, ref p, lengthSize);
                    if (id == wantedId && e == 0 && construction == 0)
                        return new ByteRange((long)(baseOffset + offset), (long)length);
                }
            }
        }
        catch (IndexOutOfRangeException) { }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }

    static uint Read(byte[] c, ref int p, int size)
    {
        ulong v = 0;
        for (int i = 0; i < size; i++) v = (v << 8) | c[p + i];
        p += size;
        return (uint)v;
    }
}
