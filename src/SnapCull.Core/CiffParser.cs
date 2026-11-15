using System.Buffers.Binary;
using System.Text;

namespace SnapCull.Core;

/// <summary>
/// Reads camera info from Canon's original RAW format, CRW (CIFF), used by cameras such as the EOS 10D, 300D and D60.
/// A CIFF file is a tree of "heaps"; each heap ends with a pointer to a table of 10-byte records.
/// </summary>
internal static class CiffParser
{
    public static bool IsCiff(ReadOnlySpan<byte> head) =>
        head.Length >= 14 && head[0] == 'I' && head[1] == 'I' && head.Slice(6, 8).SequenceEqual("HEAPCCDR"u8);

    public static ExifData Read(Stream s)
    {
        var exif = new ExifData();
        var header = Io.Read(s, 0, 6);
        if (header is null) return exif;
        long headerLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2));
        ReadHeap(s, headerLength, s.Length, exif, depth: 0);
        return exif;
    }

    static void ReadHeap(Stream s, long start, long end, ExifData exif, int depth)
    {
        if (depth > 4 || end - start < 6) return;
        var tail = Io.Read(s, end - 4, 4);
        if (tail is null) return;
        long table = start + BinaryPrimitives.ReadUInt32LittleEndian(tail);
        var countBytes = Io.Read(s, table, 2);
        if (countBytes is null) return;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(countBytes);
        if (count > 1000) return;
        var records = Io.Read(s, table + 2, count * 10);
        if (records is null) return;

        for (int i = 0; i < count; i++)
        {
            var r = records.AsSpan(i * 10, 10);
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(r);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(r[2..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(r[6..]);
            int location = tag & 0xC000;   // 0x4000 = value stored in the record itself
            int kind = tag & 0x3800;       // 0x2800 / 0x3000 = sub-heap
            int id = tag & 0x3FFF;
            if (location != 0) continue;

            long dataStart = start + offset;
            if (kind is 0x2800 or 0x3000)
            {
                ReadHeap(s, dataStart, dataStart + size, exif, depth + 1);
                continue;
            }
            if (size > 4096) continue;
            var data = Io.Read(s, dataStart, (int)size);
            if (data is null) continue;

            switch (id)
            {
                case 0x080A: // make and model, two zero-terminated strings
                    var parts = Encoding.Latin1.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0) exif.Make ??= parts[0].Trim();
                    if (parts.Length > 1) exif.Model ??= parts[1].Trim();
                    break;
                case 0x180E when data.Length >= 4: // capture time: seconds since 1970, local time
                    uint seconds = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    if (seconds > 0) exif.DateTaken ??= DateTime.UnixEpoch.AddSeconds(seconds);
                    break;
                case 0x1810 when data.Length >= 16: // image spec: width, height, pixel aspect, rotation
                    exif.SetSizeIfUnknown((int)BinaryPrimitives.ReadUInt32LittleEndian(data), (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
                    exif.Orientation = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12)) switch
                    {
                        90 => 6, 180 => 3, 270 => 8, -90 => 8, _ => exif.Orientation,
                    };
                    break;
            }
        }
    }
}
