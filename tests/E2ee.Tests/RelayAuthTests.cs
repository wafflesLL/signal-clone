// Relay authentication: requests must be signed by the account's identity key, fresh, and unique.

using System.Net;
using System.Net.Http.Headers;
using E2ee.Wire;
using NSec.Cryptography;
using P = E2ee.Primitives;

namespace E2ee.Tests;

public sealed class RelayAuthTests : IDisposable
{
    private readonly RelayFixture _relay = new();

    public void Dispose() => _relay.Dispose();

    private static HttpRequestMessage Signed(HttpMethod method, string path, string user, Key key, long? ts = null, byte[]? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            req.Content = new ByteArrayContent(body);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        var t = ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        req.Headers.Add(RelayAuth.UserHeader, user);
        req.Headers.Add(RelayAuth.TimestampHeader, t.ToString());
        req.Headers.Add(RelayAuth.SignatureHeader, Convert.ToBase64String(P.Sign(key, RelayAuth.Canonical(method.Method, path, user, t, body ?? []))));
        return req;
    }

    private async Task<(HttpClient Http, Key Key)> RegisterRaw(string user)
    {
        var http = _relay.Factory.CreateClient();
        var id = X3dh.GenerateIdentity();
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new RegisterRequest(user, P.SigPubBytes(id.SigPriv), P.DhPubBytes(id.DhPriv)), WireJson.Options);
        var resp = await http.SendAsync(Signed(HttpMethod.Post, "/v1/accounts", user, id.SigPriv, body: body));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (http, id.SigPriv);
    }

    [Fact]
    public async Task UnsignedRequestIsRejected()
    {
        var http = _relay.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/messages")).StatusCode);
    }

    [Fact]
    public async Task SignedRequestIsAccepted()
    {
        var (http, key) = await RegisterRaw("alice");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Signed(HttpMethod.Get, "/v1/messages", "alice", key))).StatusCode);
    }

    [Fact]
    public async Task WrongKeyIsRejected()
    {
        var (http, _) = await RegisterRaw("alice");
        var other = P.SigGenerate();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Signed(HttpMethod.Get, "/v1/messages", "alice", other))).StatusCode);
    }

    [Fact]
    public async Task ReadingSomeoneElsesInboxIsRejected()
    {
        var (http, aliceKey) = await RegisterRaw("alice");
        await RegisterRaw("bob");
        // Alice signs a request claiming to be bob.
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Signed(HttpMethod.Get, "/v1/messages", "bob", aliceKey))).StatusCode);
    }

    [Fact]
    public async Task StaleTimestampIsRejected()
    {
        var (http, key) = await RegisterRaw("alice");
        var old = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Signed(HttpMethod.Get, "/v1/messages", "alice", key, old))).StatusCode);
    }

    [Fact]
    public async Task ReplayedRequestIsRejected()
    {
        var (http, key) = await RegisterRaw("alice");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Signed(HttpMethod.Get, "/v1/messages", "alice", key, ts))).StatusCode);
        // Ed25519 is deterministic, so the same request has the same signature.
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Signed(HttpMethod.Get, "/v1/messages", "alice", key, ts))).StatusCode);
    }

    [Fact]
    public async Task SignatureCoversThePath()
    {
        var (http, key) = await RegisterRaw("alice");
        var req = Signed(HttpMethod.Get, "/v1/messages", "alice", key);
        req.RequestUri = new Uri("/v1/keys/status", UriKind.Relative);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task BadSignedPrekeyUploadIsRejected()
    {
        var (http, key) = await RegisterRaw("alice");
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new UploadKeysRequest(new SignedPrekeyDto(1, new byte[32], new byte[64]), []), WireJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Signed(HttpMethod.Put, "/v1/keys", "alice", key, body: body))).StatusCode);
    }
}
