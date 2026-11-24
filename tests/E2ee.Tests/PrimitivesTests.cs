// Known-answer tests for the primitive layer (RFC 7748, RFC 8032, RFC 5869).

using System.Security.Cryptography;
using E2ee;
using P = E2ee.Primitives;

namespace E2ee.Tests;

public class PrimitivesTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void X25519_Rfc7748_Sec6_1()
    {
        var a = P.DhPrivFromBytes(H("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a"));
        var b = P.DhPrivFromBytes(H("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb"));
        Assert.Equal(H("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"), P.DhPubBytes(a));
        Assert.Equal(H("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"), P.DhPubBytes(b));
        var shared = H("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");
        Assert.Equal(shared, P.Dh(a, b.PublicKey));
        Assert.Equal(shared, P.Dh(b, a.PublicKey));
    }

    [Fact]
    public void X25519_RejectsLowOrderPoint()
    {
        var a = P.DhGenerate();
        // The all-zero u-coordinate is a low-order point; the shared secret would be all zeros.
        var zero = P.DhPubFromBytes(new byte[32]);
        Assert.ThrowsAny<CryptographicException>(() => P.Dh(a, zero));
    }

    [Fact]
    public void Ed25519_Rfc8032_Test1()
    {
        var sk = P.SigPrivFromBytes(H("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"));
        Assert.Equal(H("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"), P.SigPubBytes(sk));
        var sig = P.Sign(sk, []);
        Assert.Equal(H(
            "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555" +
            "fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"), sig);
        Assert.True(P.Verify(sk.PublicKey, sig, []));
        Assert.False(P.Verify(sk.PublicKey, sig, "x"u8.ToArray()));
    }

    [Fact]
    public void Hkdf_Rfc5869_Case1()
    {
        var okm = P.Hkdf(
            ikm: Enumerable.Repeat((byte)0x0b, 22).ToArray(),
            length: 42,
            salt: H("000102030405060708090a0b0c"),
            info: H("f0f1f2f3f4f5f6f7f8f9"));
        Assert.Equal(H("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"), okm);
    }

    [Fact]
    public void Hkdf_Rfc5869_Case3_NoSalt()
    {
        var okm = P.Hkdf(Enumerable.Repeat((byte)0x0b, 22).ToArray(), 42, null, []);
        Assert.Equal(H("8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8"), okm);
    }

    [Fact]
    public void KdfCk_SeparatesKeys()
    {
        var (ck2, mk) = P.KdfCk(new byte[32]);
        Assert.NotEqual(ck2, mk);
        Assert.Equal(32, ck2.Length);
        Assert.Equal(32, mk.Length);
    }

    [Fact]
    public void Aead_RoundtripAndTamper()
    {
        var key = Enumerable.Repeat((byte)0x01, 32).ToArray();
        var ct = P.AeadEncrypt(key, "hello"u8.ToArray(), "ad"u8.ToArray());
        Assert.Equal("hello"u8.ToArray(), P.AeadDecrypt(key, ct, "ad"u8.ToArray()));
        Assert.Throws<AuthenticationTagMismatchException>(() => P.AeadDecrypt(key, ct, "other-ad"u8.ToArray()));
        var flipped = (byte[])ct.Clone();
        flipped[^1] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => P.AeadDecrypt(key, flipped, "ad"u8.ToArray()));
    }
}
