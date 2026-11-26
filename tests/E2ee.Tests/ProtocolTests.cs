using System.Security.Cryptography;
using System.Text;
using E2ee;
using P = E2ee.Primitives;

namespace E2ee.Tests;

public class ProtocolTests
{
    private sealed record Session(LocalPrekeyStore Store, InitialMessage Initial, RatchetState Alice, RatchetState Bob, byte[] Ad);

    private static Session Setup(int opks = 1)
    {
        var a = X3dh.GenerateIdentity();
        var b = X3dh.GenerateIdentity();
        var (store, bundles) = X3dh.GeneratePrekeys(b, opks);
        var bundle = bundles[0];
        var (skA, adA, ek) = X3dh.Initiate(a, bundle);
        var initial = new InitialMessage(P.SigPubBytes(a.SigPriv), P.DhPubBytes(a.DhPriv), P.DhPubBytes(ek), 1, bundle.OneTimePrekeyId, []);
        var (skB, adB) = X3dh.Respond(store, initial);
        Assert.Equal(skA, skB);
        Assert.Equal(adA, adB);
        return new Session(store, initial, Ratchet.InitAlice(skA, bundle.SignedPrekeyPub), Ratchet.InitBob(skB, store.SignedPrekeys[1]), adA);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void X3dh_BothSidesDeriveSameKey(int opks) => Setup(opks);

    [Fact]
    public void X3dh_RejectsBadSignature()
    {
        var a = X3dh.GenerateIdentity();
        var (_, bundles) = X3dh.GeneratePrekeys(X3dh.GenerateIdentity());
        bundles[0].SignedPrekeySig = new byte[64];
        Assert.Throws<ProtocolException>(() => X3dh.Initiate(a, bundles[0]));
    }

    [Fact]
    public void X3dh_OneTimePrekeyIsConsumed()
    {
        var s = Setup();
        X3dh.ConsumeOneTimePrekey(s.Store, s.Initial.OneTimePrekeyId);
        Assert.Throws<ProtocolException>(() => X3dh.Respond(s.Store, s.Initial));
    }

    [Fact]
    public void Ratchet_PingPongAndReplay()
    {
        var s = Setup();
        for (var i = 0; i < 100; i++)
        {
            var (sender, receiver) = i % 2 == 0 ? (s.Alice, s.Bob) : (s.Bob, s.Alice);
            var (header, ct) = Ratchet.Encrypt(sender, Utf8($"message {i}"), s.Ad);
            Assert.Equal(Utf8($"message {i}"), Ratchet.Decrypt(receiver, header, ct, s.Ad));
            Assert.Throws<ProtocolException>(() => Ratchet.Decrypt(receiver, header, ct, s.Ad));
        }
    }

    [Fact]
    public void Ratchet_OutOfOrderAndTamperRollback()
    {
        var s = Setup();
        var messages = Enumerable.Range(0, 5).Select(i => Ratchet.Encrypt(s.Alice, Utf8(i.ToString()), s.Ad)).ToList();
        var (h4, ct4) = messages[4];
        var tampered = (byte[])ct4.Clone();
        tampered[^1] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => Ratchet.Decrypt(s.Bob, h4, tampered, s.Ad));
        Assert.Null(s.Bob.DHr); // state rolled back
        foreach (var i in new[] { 4, 0, 2, 1, 3 })
        {
            var (h, ct) = messages[i];
            Assert.Equal(Utf8(i.ToString()), Ratchet.Decrypt(s.Bob, h, ct, s.Ad));
        }
    }

    [Fact]
    public void Ratchet_EnforcesMaxSkip()
    {
        var s = Setup();
        var (h, ct) = Ratchet.Encrypt(s.Alice, "hi"u8.ToArray(), s.Ad);
        h.N = Ratchet.MaxSkip + 1;
        Assert.Throws<ProtocolException>(() => Ratchet.Decrypt(s.Bob, h, ct, s.Ad));
    }

    [Fact]
    public void Header_RoundTrips()
    {
        var h = new Header(new byte[32], 7, 42);
        var d = Header.Decode(h.Encode());
        Assert.Equal(h.DhPub, d.DhPub);
        Assert.Equal(7, d.PN);
        Assert.Equal(42, d.N);
        Assert.Throws<ProtocolException>(() => Header.Decode(new byte[39]));
    }
}
