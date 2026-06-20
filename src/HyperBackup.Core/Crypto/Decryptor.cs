using System.Security.Cryptography;
using System.Text;
using HyperBackup.Core.Compression;

namespace HyperBackup.Core.Crypto;

/// <summary>
/// Reverses the per-chunk and filename transforms applied by HyperBackup when
/// encryption/compression are enabled.
///
/// Chunk pipeline (forward): original -> [LZ4 block compress] -> PKCS7 pad ->
/// AES-256-CBC encrypt -> store (+ 4-byte trailing tag, not part of chunk_size).
/// We reverse it: AES-256-CBC decrypt (PKCS7) -> [LZ4 decompress to dedup_size].
/// </summary>
public sealed class Decryptor
{
    private readonly VersionKey _versionKey;
    private readonly bool _compressed;

    public Decryptor(VersionKey versionKey, bool compressed)
    {
        _versionKey = versionKey;
        _compressed = compressed;
    }

    /// <summary>
    /// Decrypt (and decompress) a chunk's stored ciphertext into the original bytes.
    /// </summary>
    /// <param name="ciphertext">Exactly chunk_size bytes read from the bucket (excludes the 4-byte tag).</param>
    /// <param name="originalSize">dedup_size: the original uncompressed length.</param>
    public byte[] DecryptChunk(byte[] ciphertext, int originalSize)
    {
        // AesDecrypt strips PKCS7, yielding either the raw original (small/incompressible
        // chunks are stored uncompressed even in a compressed repo) or an LZ4 block.
        var plain = AesDecrypt(ciphertext, _versionKey.FileKey, _versionKey.FileIv);
        if (!_compressed || plain.Length == originalSize)
            return plain;
        return Lz4Block.Decompress(plain, originalSize);
    }

    /// <summary>
    /// Decrypt the AES layer of a file-pool payload, returning the XZ stream that the
    /// caller then decompresses. File pool: original -> XZ compress -> AES encrypt.
    /// </summary>
    public byte[] DecryptFilePoolPayload(byte[] ciphertext) =>
        AesDecrypt(ciphertext, _versionKey.FileKey, _versionKey.FileIv);

    private static byte[] AesDecrypt(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
    }

    /// <summary>
    /// Decrypt a filename from version_list.file_name. Filenames are AES-256-CBC
    /// encrypted then base64-encoded with a modified alphabet ('+' and '_' as
    /// altchars, i.e. '/' replaced by '_').
    /// </summary>
    public static string DecryptFilename(string encoded, byte[] filenameKey, byte[] filenameIv)
    {
        var standardBase64 = encoded.Replace('_', '/');
        var ciphertext = Convert.FromBase64String(standardBase64);
        var plain = AesDecrypt(ciphertext, filenameKey, filenameIv);
        return Encoding.UTF8.GetString(plain);
    }
}
