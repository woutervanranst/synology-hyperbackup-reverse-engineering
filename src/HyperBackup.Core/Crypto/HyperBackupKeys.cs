using System.Security.Cryptography;
using System.Text;
using Sodium;

namespace HyperBackup.Core.Crypto;

/// <summary>The per-version AES-256-CBC parameters used to encrypt chunk/file data.</summary>
public sealed record VersionKey(byte[] FileKey, byte[] FileIv);

/// <summary>
/// Implements HyperBackup's encryption key hierarchy.
///
/// IMPORTANT (reverse-engineered against a real backup + the exported key): the
/// per-version keys are wrapped with NaCl <c>crypto_box_seal</c> (libsodium sealed
/// boxes: X25519 + XSalsa20-Poly1305), NOT RSA as the README originally guessed.
/// The "encryption key" Synology exports is a 32-byte X25519 private key; the
/// repository's <c>public.pem.1</c> is the matching 32-byte X25519 public key.
///
///   vkey.rsa_vkey    (80 bytes) = sealed_box(file_key[32])  = ephemeral_pk[32] + ct[32] + mac[16]
///   vkey.rsa_vkey_iv (64 bytes) = sealed_box(file_iv[16])   = ephemeral_pk[32] + ct[16] + mac[16]
///   file_key = crypto_box_seal_open(rsa_vkey)      -> 32-byte AES-256 key
///   file_iv  = crypto_box_seal_open(rsa_vkey_iv)   -> 16-byte AES IV
///
/// On a local backup, <c>encKeys.1</c> stores this 32-byte private key AES-encrypted
/// with the password; on a cloud backup it holds only a 16-byte header, so the
/// exported key must be supplied.
/// </summary>
public sealed class HyperBackupKeys
{
    public static readonly byte[] PasswordSalt = "5mNgudh053SUoMrZxoKG8GUWyj6kEtGO"u8.ToArray();
    public static readonly byte[] UnikeySalt1 = "CIpfMargmxetgFtkBmG3KqEiQ6qfqZgF"u8.ToArray();
    public static readonly byte[] UnikeySalt2 = "kkE7sRZRvnbVlJFofhD7WCXumXBGyzki"u8.ToArray();
    public static readonly byte[] FileKeySalt = "8Llx6OSaDPzbwCkjG8eYc64GZGMIlMXm"u8.ToArray();

    private readonly string _unikey;
    private readonly byte[] _privateKey; // 32-byte X25519 private key
    private readonly byte[] _publicKey;  // 32-byte X25519 public key (derived)

    private HyperBackupKeys(string unikey, byte[] privateKey, byte[] publicKey)
    {
        _unikey = unikey;
        _privateKey = privateKey;
        _publicKey = publicKey;
    }

    public byte[] PublicKey => _publicKey;

    public static byte[] DerivePasswordKey(string password) =>
        SHA256.HashData([.. PasswordSalt, .. Encoding.UTF8.GetBytes(password)]);

    /// <summary>Build the key set from the exported 32-byte X25519 private key.</summary>
    public static HyperBackupKeys FromExportedKey(string unikey, byte[] privateKey)
    {
        if (privateKey.Length != 32)
            throw new InvalidOperationException(
                $"Expected a 32-byte X25519 private key (the exported Synology encryption key), " +
                $"got {privateKey.Length} bytes.");
        var publicKey = ScalarMult.Base(privateKey);
        return new HyperBackupKeys(unikey, privateKey, publicKey);
    }

    /// <summary>
    /// Build the key set from a local-format <c>encKeys.1</c> payload (the X25519
    /// private key, AES-encrypted with the password) plus the password.
    /// </summary>
    public static HyperBackupKeys FromEncKeys(string unikey, byte[] encKeysPayload, string password)
    {
        if (encKeysPayload.Length <= 16)
            throw new InvalidOperationException(
                "encKeys.1 contains only a header (no key material) — this is a cloud-format backup. " +
                "Supply the exported X25519 key via FromExportedKey().");

        var passwordKey = DerivePasswordKey(password);
        var iv = MD5.HashData([.. Encoding.UTF8.GetBytes(unikey), .. UnikeySalt1]);
        byte[] keyBytes;
        try
        {
            keyBytes = DecryptAesCbc(encKeysPayload[16..], passwordKey, iv);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Failed to decrypt encKeys.1 — wrong password?", ex);
        }
        // The decrypted payload begins with the 32-byte X25519 private key.
        return FromExportedKey(unikey, keyBytes[..32]);
    }

    /// <summary>Unwrap a per-version AES key/IV from a <c>vkey</c> row, verifying the checksum.</summary>
    public VersionKey UnwrapVersionKey(byte[] sealedKey, byte[] sealedIv, byte[]? checksum)
    {
        if (checksum is not null)
        {
            var expected = MD5.HashData([.. sealedKey, .. FileKeySalt, .. sealedIv]);
            if (!expected.AsSpan().SequenceEqual(checksum))
                throw new InvalidDataException("vkey checksum mismatch — wrong vkey row or corrupt data.");
        }

        // crypto_box_seal_open — authenticated, so a wrong key throws here.
        var fileKey = SealedPublicKeyBox.Open(sealedKey, _privateKey, _publicKey);
        var fileIv = SealedPublicKeyBox.Open(sealedIv, _privateKey, _publicKey);
        return new VersionKey(fileKey, fileIv);
    }

    /// <summary>Derive the filename encryption key/IV (best-effort; see FilenameCrypto notes).</summary>
    public (byte[] Key, byte[] Iv) FilenameKey()
    {
        var key = SHA256.HashData([.. _privateKey, .. Encoding.UTF8.GetBytes(_unikey)]);
        var iv = MD5.HashData([.. Encoding.UTF8.GetBytes(_unikey), .. UnikeySalt2]);
        return (key, iv);
    }

    private static byte[] DecryptAesCbc(byte[] data, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
    }
}
