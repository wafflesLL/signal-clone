// Wire format shared by the relay (E2ee.Server) and its clients (E2ee.Messaging).
// JSON, snake_case, byte arrays as base64. Nothing here is secret: the relay sees all of it.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace E2ee.Wire;

public sealed record RegisterRequest(string Username, byte[] IdentitySigPub, byte[] IdentityDhPub);

public sealed record SignedPrekeyDto(uint Id, byte[] Pub, byte[] Sig);

public sealed record OneTimePrekeyDto(uint Id, byte[] Pub);

/// <summary>Replaces the signed prekey (if given) and adds one-time prekeys.</summary>
public sealed record UploadKeysRequest(SignedPrekeyDto? SignedPrekey, List<OneTimePrekeyDto> OneTimePrekeys);

public sealed record PrekeyStatusResponse(int OneTimePrekeys, uint? SignedPrekeyId);

public sealed record IdentityResponse(string Username, byte[] IdentitySigPub, byte[] IdentityDhPub);

public sealed record BundleResponse(
    string Username, byte[] IdentitySigPub, byte[] IdentityDhPub, SignedPrekeyDto SignedPrekey, OneTimePrekeyDto? OneTimePrekey);

/// <summary>X3DH first-message fields. Sent with every message until the peer replies.</summary>
public sealed record InitialDto(byte[] IdentitySigPub, byte[] IdentityDhPub, byte[] EphemeralPub, uint SignedPrekeyId, uint? OneTimePrekeyId);

public sealed record EnvelopeDto(InitialDto? Initial, byte[] Header, byte[] Ciphertext);

/// <summary>An envelope as the recipient sees it. The relay authenticates and fills in the sender.</summary>
public sealed record InboxItem(long Id, string Sender, long ReceivedAt, EnvelopeDto Envelope);

public sealed record InboxResponse(List<InboxItem> Messages);

public sealed record AckRequest(List<long> Ids);

public sealed record ErrorResponse(string Error);

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

public static partial class Usernames
{
    [GeneratedRegex("^[a-z0-9_]{3,32}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);
}

/// <summary>
/// Request authentication. Every authenticated request carries the username, a Unix
/// timestamp in milliseconds, and an Ed25519 signature by the user's identity key over
/// <see cref="Canonical"/>. The relay checks the signature against the key bound to the
/// username at registration, rejects timestamps outside <see cref="MaxSkew"/>, and
/// rejects a signature it has already seen. No passwords or tokens exist.
/// </summary>
public static class RelayAuth
{
    public const string UserHeader = "X-E2EE-User";
    public const string TimestampHeader = "X-E2EE-Timestamp";
    public const string SignatureHeader = "X-E2EE-Signature";
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static byte[] Canonical(string method, string pathAndQuery, string user, long timestampMs, ReadOnlySpan<byte> body) =>
        Encoding.UTF8.GetBytes(string.Join('\n',
            "COSC483-REQ-v1", method.ToUpperInvariant(), pathAndQuery, user,
            timestampMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToHexString(SHA256.HashData(body))));
}
