using K4os.Compression.LZ4;

namespace HyperBackup.Core.Compression;

/// <summary>
/// Decompressor for the LZ4 <b>block</b> format (no frame header), which is what
/// HyperBackup applies to chunk data when data_compress_type=1 (before AES
/// encryption). The original/uncompressed size is known from the chunk index
/// (dedup_size), so the caller passes it as <c>expectedSize</c>.
/// Backed by the K4os LZ4 codec.
/// </summary>
public static class Lz4Block
{
    public static byte[] Decompress(ReadOnlySpan<byte> input, int expectedSize)
    {
        var output = new byte[expectedSize];
        var decoded = LZ4Codec.Decode(input, output);
        if (decoded != expectedSize)
            throw new InvalidDataException(
                $"LZ4 block decompression produced {decoded} bytes, expected {expectedSize}.");
        return output;
    }
}
