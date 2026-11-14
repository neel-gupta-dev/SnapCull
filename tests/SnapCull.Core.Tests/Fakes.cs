using System.Buffers.Binary;
using System.Text;

namespace SnapCull.Core.Tests;

/// <summary>Builds tiny but structurally valid JPEG, TIFF and CR3 files, so tests don't need real camera files.</summary>
internal static class Fake
{
    /// <summary>A JPEG with the right markers and size. Its pixel data is noise, which is fine: nothing here decodes pixels.</summary>
    public static byte[] Jpeg(int width, int height, byte frameMarker = 0xC0, byte[]? exifTiff = null, int dataBytes = 300)
    {
        var b = new List<byte> { 0xFF, 0xD8 };
        b.AddRange([0xFF, 0xE0, 0x00, 0x10, .. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        if (exifTiff is not null)
        {
            int len = 2 + 6 + exifTiff.Length;
            b.AddRange([0xFF, 0xE1, (byte)(len >> 8), (byte)len, .. "Exif\0\0"u8, .. exifTiff]);
        }
        b.AddRange([0xFF, 0xDB, 0x00, 0x43, 0x00, .. new byte[64]]);
        b.AddRange([0xFF, frameMarker, 0x00, 0x0B, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0]);
        b.AddRange([0xFF, 0xC4, 0x00, 0x05, 0x00, 0x00, 0x00]);
        AddScan(b, dataBytes);
        if (frameMarker == 0xC2)
        {
            // Progressive JPEGs have several scans with tables in between.
            b.AddRange([0xFF, 0xC4, 0x00, 0x05, 0x10, 0x00, 0x00]);
            AddScan(b, dataBytes);
        }
        b.AddRange([0xFF, 0xD9]);
        return [.. b];
    }

    static void AddScan(List<byte> b, int dataBytes)
    {
        b.AddRange([0xFF, 0xDA, 0x00, 0x08, 1, 1, 0x00, 0, 63, 0]);
        for (int i = 0; i < dataBytes; i++) b.Add((byte)(i * 7 % 255)); // never 0xFF
        b.AddRange([0xFF, 0x00, 0x12, 0xFF, 0xD0, 0x34]); // a stuffed byte and a restart marker
    }

    public static byte[] Bytes(int count, int seed)
    {
        var data = new byte[count];
        new Random(seed).NextBytes(data);
        for (int i = 0; i < data.Length; i++) if (data[i] == 0xFF) data[i] = 0xFE; // no accidental markers
        return data;
    }

    public static byte[] Box(string type, params byte[][] content)
    {
        int size = 8 + content.Sum(c => c.Length);
        var b = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        int p = 8;
        foreach (var c in content) { c.CopyTo(b, p); p += c.Length; }
        return b;
    }

    public static byte[] BE32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    public static byte[] BE64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); return b; }
}

internal sealed record TiffEntry(ushort Tag, ushort Type, uint Count, byte[] Value);

/// <summary>Writes a TIFF file. Blobs and IFDs are appended in order; IFDs can point at anything written before them.</summary>
internal sealed class TiffBuilder
{
    readonly List<byte> _b = [];
    readonly bool _bigEndian;

    public TiffBuilder(bool bigEndian = false)
    {
        _bigEndian = bigEndian;
        _b.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        _b.AddRange(U16(42));
        _b.AddRange(U32(0)); // first IFD offset, set by SetFirstIfd
    }

    public TiffEntry Ascii(ushort tag, string s) { var v = Encoding.ASCII.GetBytes(s + "\0"); return new(tag, 2, (uint)v.Length, v); }
    public TiffEntry Short(ushort tag, ushort v) => new(tag, 3, 1, U16(v));
    public TiffEntry Long(ushort tag, uint v) => new(tag, 4, 1, U32(v));
    public TiffEntry Rational(ushort tag, uint num, uint den) => new(tag, 5, 1, [.. U32(num), .. U32(den)]);

    public uint AddBlob(byte[] data)
    {
        Align();
        uint offset = (uint)_b.Count;
        _b.AddRange(data);
        return offset;
    }

    public uint AddIfd(IEnumerable<TiffEntry> entries, uint next = 0)
    {
        Align();
        var list = entries.OrderBy(e => e.Tag).ToList();
        uint ifd = (uint)_b.Count;
        uint dataStart = ifd + 2 + (uint)list.Count * 12 + 4;
        var data = new List<byte>();

        _b.AddRange(U16((ushort)list.Count));
        foreach (var e in list)
        {
            _b.AddRange(U16(e.Tag));
            _b.AddRange(U16(e.Type));
            _b.AddRange(U32(e.Count));
            if (e.Value.Length <= 4)
            {
                var inline = new byte[4];
                e.Value.CopyTo(inline, 0);
                _b.AddRange(inline);
            }
            else
            {
                _b.AddRange(U32(dataStart + (uint)data.Count));
                data.AddRange(e.Value);
                if (data.Count % 2 == 1) data.Add(0);
            }
        }
        _b.AddRange(U32(next));
        _b.AddRange(data);
        return ifd;
    }

    public void SetFirstIfd(uint offset)
    {
        var v = U32(offset);
        for (int i = 0; i < 4; i++) _b[4 + i] = v[i];
    }

    public byte[] ToArray() => [.. _b];

    void Align() { if (_b.Count % 2 == 1) _b.Add(0); }

    byte[] U16(ushort v)
    {
        var b = new byte[2];
        if (_bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, v); else BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    byte[] U32(uint v)
    {
        var b = new byte[4];
        if (_bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v); else BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("snapcull-test-").FullName;

    public string File(string name, byte[]? content = null)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, content ?? Encoding.UTF8.GetBytes(name));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
