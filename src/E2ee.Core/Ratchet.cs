// Educational Double Ratchet, following Signal's basic DH/chain ratchet.
// Field names mirror the spec (DHs, DHr, RK, CKs, CKr, Ns, Nr, PN, MKSKIPPED).

using System.Buffers.Binary;
using NSec.Cryptography;
using P = E2ee.Primitives;

namespace E2ee;

public sealed class Header(byte[] dhPub, long pn, long n)
{
    public const int Length = 40;

    public byte[] DhPub { get; set; } = dhPub;
    public long PN { get; set; } = pn;
    public long N { get; set; } = n;

    public byte[] Encode()
    {
        if (DhPub.Length != 32 || Math.Min(PN, N) < 0 || Math.Max(PN, N) > uint.MaxValue)
            throw new ProtocolException("invalid header");
        var output = new byte[Length];
        DhPub.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(32), (uint)PN);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(36), (uint)N);
        return output;
    }

    public static Header Decode(byte[] data)
    {
        if (data.Length != Length) throw new ProtocolException("header must be 40 bytes");
        return new Header(data[..32], BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(32)), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(36)));
    }
}

public sealed class RatchetState
{
    public required Key DHs { get; set; }
    public byte[]? DHr { get; set; }
    public required byte[] RK { get; set; }
    public byte[]? CKs { get; set; }
    public byte[]? CKr { get; set; }
    public long Ns { get; set; }
    public long Nr { get; set; }
    public long PN { get; set; }
    /// <summary>Skipped message keys indexed by (ratchet public key, message number).</summary>
    public Dictionary<(string DhPub, long N), byte[]> MKSKIPPED { get; set; } = new();

    internal RatchetState Clone() => new()
    {
        DHs = DHs, DHr = DHr, RK = RK, CKs = CKs, CKr = CKr, Ns = Ns, Nr = Nr, PN = PN,
        MKSKIPPED = new(MKSKIPPED),
    };

    internal void CommitFrom(RatchetState w)
    {
        // The old DHs is not disposed here: for Bob it starts as the shared signed prekey,
        // which other sessions still need.
        (DHs, DHr, RK, CKs, CKr, Ns, Nr, PN, MKSKIPPED) = (w.DHs, w.DHr, w.RK, w.CKs, w.CKr, w.Ns, w.Nr, w.PN, w.MKSKIPPED);
    }
}

public static class Ratchet
{
    public const int MaxSkip = 1000;

    private static (string, long) SkipKey(byte[] dhPub, long n) => (Convert.ToHexString(dhPub), n);

    public static RatchetState InitAlice(byte[] sk, byte[] bobDhPub)
    {
        var dhs = P.DhGenerate();
        var (rk, cks) = P.KdfRk(sk, P.Dh(dhs, P.DhPubFromBytes(bobDhPub)));
        return new RatchetState { DHs = dhs, DHr = bobDhPub, RK = rk, CKs = cks };
    }

    public static RatchetState InitBob(byte[] sk, Key bobDhKeyPair) => new() { DHs = bobDhKeyPair, RK = sk };

    public static (Header Header, byte[] Ciphertext) Encrypt(RatchetState state, byte[] plaintext, byte[] ad)
    {
        if (state.CKs is null) throw new ProtocolException("no sending chain yet");
        var (ck, mk) = P.KdfCk(state.CKs);
        var header = new Header(P.DhPubBytes(state.DHs), state.PN, state.Ns);
        var ct = P.AeadEncrypt(mk, plaintext, P.Concat(ad, header.Encode()));
        state.CKs = ck;
        state.Ns++;
        return (header, ct);
    }

    private static void Skip(RatchetState s, long until)
    {
        if (until < s.Nr || until - s.Nr > MaxSkip || s.MKSKIPPED.Count + until - s.Nr > MaxSkip)
            throw new ProtocolException("invalid or excessive skipped messages");
        if (s.CKr is null)
        {
            if (until != s.Nr) throw new ProtocolException("missing receiving chain");
            return;
        }
        while (s.Nr < until)
        {
            var (ck, mk) = P.KdfCk(s.CKr);
            s.CKr = ck;
            s.MKSKIPPED[SkipKey(s.DHr!, s.Nr)] = mk;
            s.Nr++;
        }
    }

    private static void DhRatchet(RatchetState s, byte[] newPub)
    {
        (s.PN, s.Ns, s.Nr) = (s.Ns, 0, 0);
        s.DHr = newPub;
        var pub = P.DhPubFromBytes(newPub);
        (s.RK, s.CKr) = P.KdfRk(s.RK, P.Dh(s.DHs, pub));
        s.DHs = P.DhGenerate();
        (s.RK, s.CKs) = P.KdfRk(s.RK, P.Dh(s.DHs, pub));
    }

    /// <summary>
    /// Decrypts on a working copy and commits only on success, so a forged or
    /// tampered message cannot advance or corrupt the session state.
    /// </summary>
    public static byte[] Decrypt(RatchetState state, Header header, byte[] ciphertext, byte[] ad)
    {
        var w = state.Clone();
        var fullAd = P.Concat(ad, header.Encode());
        byte[] pt;
        var keyId = SkipKey(header.DhPub, header.N);
        if (w.MKSKIPPED.Remove(keyId, out var skippedMk))
        {
            pt = P.AeadDecrypt(skippedMk, ciphertext, fullAd);
        }
        else
        {
            if (w.DHr is null || !header.DhPub.AsSpan().SequenceEqual(w.DHr))
            {
                Skip(w, header.PN);
                DhRatchet(w, header.DhPub);
            }
            if (header.N < w.Nr) throw new ProtocolException("replayed or expired message");
            Skip(w, header.N);
            if (w.CKr is null) throw new ProtocolException("no receiving chain");
            var (ck, mk) = P.KdfCk(w.CKr);
            w.CKr = ck;
            w.Nr++;
            pt = P.AeadDecrypt(mk, ciphertext, fullAd);
        }
        state.CommitFrom(w);
        return pt;
    }
}
