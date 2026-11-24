// Educational X3DH variant with a separate Ed25519 signing identity.

using System.Buffers.Binary;
using NSec.Cryptography;
using P = E2ee.Primitives;

namespace E2ee;

public sealed record IdentityKeys(Key SigPriv, Key DhPriv);

public sealed class PrekeyBundle
{
    public required byte[] IdentitySigPub { get; init; }
    public required byte[] IdentityDhPub { get; init; }
    public required uint SignedPrekeyId { get; init; }
    public required byte[] SignedPrekeyPub { get; init; }
    public required byte[] SignedPrekeySig { get; set; }
    public uint? OneTimePrekeyId { get; init; }
    public byte[]? OneTimePrekeyPub { get; init; }
}

public sealed class LocalPrekeyStore(IdentityKeys identity)
{
    public IdentityKeys Identity { get; } = identity;
    public Dictionary<uint, Key> SignedPrekeys { get; } = new();
    public Dictionary<uint, Key> OneTimePrekeys { get; } = new();
}

public sealed record InitialMessage(
    byte[] IdentitySigPub,
    byte[] IdentityDhPub,
    byte[] EphemeralPub,
    uint SignedPrekeyId,
    uint? OneTimePrekeyId,
    byte[] Ciphertext);

public static class X3dh
{
    public static readonly byte[] Info = "COSC483-E2EE-X3DH"u8.ToArray();
    private static readonly byte[] F = Enumerable.Repeat((byte)0xFF, 32).ToArray();

    public static IdentityKeys GenerateIdentity() => new(P.SigGenerate(), P.DhGenerate());

    private static byte[] SignedData(uint spkId, byte[] pub)
    {
        var id = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(id, spkId);
        return P.Concat("COSC483-SPK-v1"u8.ToArray(), id, pub);
    }

    public static (LocalPrekeyStore Store, List<PrekeyBundle> Bundles) GeneratePrekeys(IdentityKeys identity, int nOneTime = 20)
    {
        if (nOneTime < 0) throw new ArgumentOutOfRangeException(nameof(nOneTime), "negative count");
        var spk = P.DhGenerate();
        var store = new LocalPrekeyStore(identity);
        store.SignedPrekeys[1] = spk;
        var sig = P.Sign(identity.SigPriv, SignedData(1, P.DhPubBytes(spk)));

        PrekeyBundle Make(uint? opkId = null, byte[]? opkPub = null) => new()
        {
            IdentitySigPub = P.SigPubBytes(identity.SigPriv),
            IdentityDhPub = P.DhPubBytes(identity.DhPriv),
            SignedPrekeyId = 1,
            SignedPrekeyPub = P.DhPubBytes(spk),
            SignedPrekeySig = sig,
            OneTimePrekeyId = opkId,
            OneTimePrekeyPub = opkPub,
        };

        var bundles = new List<PrekeyBundle>();
        for (uint i = 1; i <= nOneTime; i++)
        {
            var opk = P.DhGenerate();
            store.OneTimePrekeys[i] = opk;
            bundles.Add(Make(i, P.DhPubBytes(opk)));
        }
        if (bundles.Count == 0) bundles.Add(Make());
        return (store, bundles);
    }

    private static byte[] Ad(byte[] aSig, byte[] aDh, byte[] bSig, byte[] bDh) =>
        P.Concat("COSC483-AD-v1"u8.ToArray(), aSig, aDh, bSig, bDh);

    /// <summary>Alice's side. Returns the shared secret, the associated data, and her ephemeral key.</summary>
    public static (byte[] Sk, byte[] Ad, Key Ek) Initiate(IdentityKeys alice, PrekeyBundle bundle)
    {
        if (bundle.OneTimePrekeyId is null != bundle.OneTimePrekeyPub is null)
            throw new ProtocolException("inconsistent OPK bundle");
        if (!P.Verify(P.SigPubFromBytes(bundle.IdentitySigPub), bundle.SignedPrekeySig, SignedData(bundle.SignedPrekeyId, bundle.SignedPrekeyPub)))
            throw new ProtocolException("invalid signed prekey signature");

        var ek = P.DhGenerate();
        var spk = P.DhPubFromBytes(bundle.SignedPrekeyPub);
        var parts = new List<byte[]>
        {
            P.Dh(alice.DhPriv, spk),                                   // DH1 = DH(IKa, SPKb)
            P.Dh(ek, P.DhPubFromBytes(bundle.IdentityDhPub)),           // DH2 = DH(EKa, IKb)
            P.Dh(ek, spk),                                              // DH3 = DH(EKa, SPKb)
        };
        if (bundle.OneTimePrekeyPub is not null)
            parts.Add(P.Dh(ek, P.DhPubFromBytes(bundle.OneTimePrekeyPub))); // DH4 = DH(EKa, OPKb)

        var sk = P.Hkdf(P.Concat([F, .. parts]), 32, null, Info);
        var ad = Ad(P.SigPubBytes(alice.SigPriv), P.DhPubBytes(alice.DhPriv), bundle.IdentitySigPub, bundle.IdentityDhPub);
        return (sk, ad, ek);
    }

    /// <summary>Bob's side. Does not consume the OPK: call <see cref="ConsumeOneTimePrekey"/>
    /// only after the first message has been authenticated.</summary>
    public static (byte[] Sk, byte[] Ad) Respond(LocalPrekeyStore bob, InitialMessage msg)
    {
        if (!bob.SignedPrekeys.TryGetValue(msg.SignedPrekeyId, out var spk))
            throw new ProtocolException("unknown signed prekey");
        Key? opk = null;
        if (msg.OneTimePrekeyId is { } opkId && !bob.OneTimePrekeys.TryGetValue(opkId, out opk))
            throw new ProtocolException("missing or consumed one-time prekey");

        var ikA = P.DhPubFromBytes(msg.IdentityDhPub);
        var ekA = P.DhPubFromBytes(msg.EphemeralPub);
        var parts = new List<byte[]> { P.Dh(spk, ikA), P.Dh(bob.Identity.DhPriv, ekA), P.Dh(spk, ekA) };
        if (opk is not null) parts.Add(P.Dh(opk, ekA));

        var sk = P.Hkdf(P.Concat([F, .. parts]), 32, null, Info);
        var ad = Ad(msg.IdentitySigPub, msg.IdentityDhPub, P.SigPubBytes(bob.Identity.SigPriv), P.DhPubBytes(bob.Identity.DhPriv));
        return (sk, ad);
    }

    public static void ConsumeOneTimePrekey(LocalPrekeyStore bob, uint? opkId)
    {
        if (opkId is { } id && bob.OneTimePrekeys.Remove(id, out var key))
            key.Dispose();
    }
}
