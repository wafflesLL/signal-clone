// Safety numbers: a 60-digit code two people compare in person or over a call. It is
// derived from both identities, so if a relay (or anyone) substitutes a key, the two
// sides see different numbers.
//
// Follows Signal's NumericFingerprintGenerator (version 0): for each party,
//   h = SHA-512(version || publicKey || stableId), then 5199 times h = SHA-512(h || publicKey),
// take the first 30 bytes as six 5-byte big-endian chunks, each mod 100000 -> 5 digits.
// The two 30-digit halves are sorted and concatenated, so both sides get the same number.
//
// Deviation: our identity is two keys (Ed25519 + X25519), so "publicKey" is sigPub || dhPub,
// and we use version 0x00C4 so these numbers can't be confused with Signal's.

using System.Security.Cryptography;
using System.Text;

namespace E2ee;

public static class SafetyNumber
{
    public const int Iterations = 5200;
    private static readonly byte[] Version = [0x00, 0xC4];

    /// <summary>The 30-digit half for one party.</summary>
    public static string Fingerprint(string username, byte[] identitySigPub, byte[] identityDhPub)
    {
        var publicKey = Primitives.Concat(identitySigPub, identityDhPub);
        var hash = SHA512.HashData(Primitives.Concat(Version, publicKey, Encoding.UTF8.GetBytes(username)));
        for (var i = 1; i < Iterations; i++) hash = SHA512.HashData(Primitives.Concat(hash, publicKey));

        var digits = new StringBuilder(30);
        for (var chunk = 0; chunk < 6; chunk++)
        {
            ulong v = 0;
            for (var b = 0; b < 5; b++) v = (v << 8) | hash[chunk * 5 + b];
            digits.Append((v % 100000).ToString("D5"));
        }
        return digits.ToString();
    }

    /// <summary>The shared 60-digit number, as 12 groups of 5 digits separated by spaces.</summary>
    public static string Compute(string userA, byte[] sigA, byte[] dhA, string userB, byte[] sigB, byte[] dhB)
    {
        var a = Fingerprint(userA, sigA, dhA);
        var b = Fingerprint(userB, sigB, dhB);
        var combined = string.CompareOrdinal(a, b) <= 0 ? a + b : b + a;
        return string.Join(' ', Enumerable.Range(0, 12).Select(i => combined.Substring(i * 5, 5)));
    }
}
