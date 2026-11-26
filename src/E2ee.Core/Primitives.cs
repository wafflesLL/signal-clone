// Thin wrappers around vetted primitives.
//
// Rule for the whole project: nothing below the protocol layer is hand-rolled.
// Every function here is a one-liner over a vetted library, so the protocol code
// (X3dh.cs, Ratchet.cs) reads like the Signal specs.
//
//   X25519, Ed25519             NSec.Cryptography (libsodium)
//   HKDF-SHA256, HMAC-SHA256    System.Security.Cryptography (.NET BCL)
//   AES-256-GCM                 System.Security.Cryptography (.NET BCL)
//
// Deviation from Signal (documented in the report): Signal signs prekeys with
// XEdDSA using the X25519 identity key. We keep two identity keys instead, an
// Ed25519 signing key and an X25519 DH key, because neither library has XEdDSA.

using System.Security.Cryptography;
using NSec.Cryptography;

namespace E2ee;

public static class Primitives
{
    public const int KeyLen = 32;
    public const int NonceLen = 12;
    public const int TagLen = 16;

    private static readonly KeyAgreementAlgorithm X25519 = KeyAgreementAlgorithm.X25519;
    private static readonly SignatureAlgorithm Ed25519 = SignatureAlgorithm.Ed25519;

    // Private keys must be exportable so tests can load fixed RFC vectors and the
    // client can persist state later. Nothing exports them over the network.
    private static KeyCreationParameters Exportable => new() { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };

    // ---------------------------------------------------------------- X25519 (DH)
    public static Key DhGenerate() => Key.Create(X25519, Exportable);

    /// <summary>X25519 shared secret. libsodium rejects the all-zero output for low-order points.</summary>
    public static byte[] Dh(Key priv, PublicKey pub)
    {
        using var shared = X25519.Agree(priv, pub, new SharedSecretCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport })
            ?? throw new CryptographicException("X25519 produced an all-zero shared secret (low-order point)");
        return shared.Export(SharedSecretBlobFormat.RawSharedSecret);
    }

    public static byte[] DhPubBytes(Key key) => DhPubBytes(key.PublicKey);
    public static byte[] DhPubBytes(PublicKey pub) => pub.Export(KeyBlobFormat.RawPublicKey);
    public static PublicKey DhPubFromBytes(byte[] data) => PublicKey.Import(X25519, data, KeyBlobFormat.RawPublicKey);
    public static Key DhPrivFromBytes(byte[] data) => Key.Import(X25519, data, KeyBlobFormat.RawPrivateKey, Exportable);
    public static byte[] DhPrivBytes(Key key) => key.Export(KeyBlobFormat.RawPrivateKey);

    // ------------------------------------------------------------ Ed25519 (sigs)
    public static Key SigGenerate() => Key.Create(Ed25519, Exportable);
    public static byte[] Sign(Key priv, byte[] msg) => Ed25519.Sign(priv, msg);
    public static bool Verify(PublicKey pub, byte[] sig, byte[] msg) => Ed25519.Verify(pub, msg, sig);
    public static byte[] SigPubBytes(Key key) => SigPubBytes(key.PublicKey);
    public static byte[] SigPubBytes(PublicKey pub) => pub.Export(KeyBlobFormat.RawPublicKey);
    public static PublicKey SigPubFromBytes(byte[] data) => PublicKey.Import(Ed25519, data, KeyBlobFormat.RawPublicKey);
    public static Key SigPrivFromBytes(byte[] data) => Key.Import(Ed25519, data, KeyBlobFormat.RawPrivateKey, Exportable);
    public static byte[] SigPrivBytes(Key key) => key.Export(KeyBlobFormat.RawPrivateKey);

    // ----------------------------------------------------------------- KDFs
    /// <summary>HKDF-SHA256. A null salt means HashLen zero bytes (RFC 5869 sec. 2.2).</summary>
    public static byte[] Hkdf(byte[] ikm, int length, byte[]? salt, byte[] info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt ?? Array.Empty<byte>(), info);

    public static readonly byte[] RatchetInfo = "COSC483-E2EE-Ratchet"u8.ToArray();

    /// <summary>Double Ratchet KDF_RK: HKDF(salt=rk, ikm=dh_out) -> (new root key, chain key).</summary>
    public static (byte[] Rk, byte[] Ck) KdfRk(byte[] rk, byte[] dhOut, byte[]? info = null)
    {
        var output = Hkdf(dhOut, 2 * KeyLen, rk, info ?? RatchetInfo);
        return (output[..KeyLen], output[KeyLen..]);
    }

    /// <summary>
    /// Double Ratchet KDF_CK, as recommended in the spec (sec. 5.2):
    /// message key = HMAC(ck, 0x01), next chain key = HMAC(ck, 0x02).
    /// </summary>
    public static (byte[] NextCk, byte[] Mk) KdfCk(byte[] ck)
    {
        var mk = HMACSHA256.HashData(ck, new byte[] { 0x01 });
        var nextCk = HMACSHA256.HashData(ck, new byte[] { 0x02 });
        return (nextCk, mk);
    }

    // ----------------------------------------------------------------- AEAD
    /// <summary>
    /// AES-256-GCM with a random 96-bit nonce prepended: nonce || ciphertext || tag.
    /// The ratchet uses each message key exactly once, so a random nonce is fine.
    /// (The spec's alternative is deriving the nonce from the message key with HKDF.)
    /// </summary>
    public static byte[] AeadEncrypt(byte[] key, byte[] plaintext, byte[] ad)
    {
        var output = new byte[NonceLen + plaintext.Length + TagLen];
        var nonce = output.AsSpan(0, NonceLen);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagLen);
        gcm.Encrypt(nonce, plaintext, output.AsSpan(NonceLen, plaintext.Length), output.AsSpan(NonceLen + plaintext.Length), ad);
        return output;
    }

    /// <summary>Throws AuthenticationTagMismatchException on tampering or wrong key/AD.</summary>
    public static byte[] AeadDecrypt(byte[] key, byte[] ciphertext, byte[] ad)
    {
        if (ciphertext.Length < NonceLen + TagLen)
            throw new AuthenticationTagMismatchException("ciphertext too short");
        var body = ciphertext.AsSpan(NonceLen, ciphertext.Length - NonceLen - TagLen);
        var plaintext = new byte[body.Length];
        using var gcm = new AesGcm(key, TagLen);
        gcm.Decrypt(ciphertext.AsSpan(0, NonceLen), body, ciphertext.AsSpan(ciphertext.Length - TagLen), plaintext, ad);
        return plaintext;
    }

    internal static byte[] Concat(params byte[][] parts)
    {
        var output = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts)
        {
            p.CopyTo(output, offset);
            offset += p.Length;
        }
        return output;
    }
}

/// <summary>Protocol-level rejection (the Python version's ValueError).</summary>
public sealed class ProtocolException(string message) : Exception(message);
