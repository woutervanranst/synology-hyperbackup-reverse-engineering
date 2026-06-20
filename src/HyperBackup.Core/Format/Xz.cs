using SharpCompress.Compressors.Xz;

namespace HyperBackup.Core.Format;

/// <summary>
/// XZ/LZMA decompression of file-pool payloads. Uses SharpCompress's pure-managed
/// <see cref="XZStream"/> so the tool has no external (CLI) dependency.
/// </summary>
public static class Xz
{
    public static byte[] Decompress(byte[] xzData)
    {
        using var input = new MemoryStream(xzData, writable: false);
        using var xz = new XZStream(input);
        using var output = new MemoryStream();
        xz.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>The XZ stream magic: FD 37 7A 58 5A 00.</summary>
    public static readonly byte[] StreamMagic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    /// <summary>Find the first occurrence of the XZ magic in a buffer, or -1.</summary>
    public static int FindStreamStart(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + StreamMagic.Length <= data.Length; i++)
        {
            if (data.Slice(i, StreamMagic.Length).SequenceEqual(StreamMagic))
                return i;
        }
        return -1;
    }
}
