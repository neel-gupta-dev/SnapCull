namespace SnapCull.Core;

internal static class Io
{
    /// <summary>Reads exactly <paramref name="count"/> bytes at <paramref name="offset"/>, or returns null if that range is outside the stream.</summary>
    public static byte[]? Read(Stream s, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > s.Length) return null;
        var buffer = new byte[count];
        s.Position = offset;
        s.ReadExactly(buffer);
        return buffer;
    }
}

/// <summary>A run of bytes inside a file.</summary>
internal readonly record struct ByteRange(long Offset, long Length);
