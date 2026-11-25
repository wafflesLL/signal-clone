// Untrusted relay. It binds each username to an identity key on first registration,
// stores public prekeys, and queues opaque envelopes until the recipient acknowledges
// them. It never sees private keys or plaintext. Requests are authenticated by an
// Ed25519 signature from the caller's identity key (see E2ee.Wire.RelayAuth).
//
// Run: dotnet run --project src/E2ee.Server --urls http://127.0.0.1:8000
// The SQLite path comes from the E2EE_DB setting (env var or --E2EE_DB=...), default relay.sqlite3.

using System.Text.Json;
using E2ee;
using E2ee.Server;
using E2ee.Wire;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(sp => new RelayStore(sp.GetRequiredService<IConfiguration>()["E2EE_DB"] ?? "relay.sqlite3"));
builder.Services.AddSingleton<ReplayGuard>();
builder.Services.AddSingleton<InboxNotifier>();
var app = builder.Build();

const int MaxBodyBytes = 256 * 1024;
const int MaxCiphertextBytes = 128 * 1024;
const int MaxPrekeysPerUpload = 200;

static IResult Error(int status, string message) => Results.Json(new ErrorResponse(message), WireJson.Options, statusCode: status);
static IResult Json(object value) => Results.Json(value, WireJson.Options);

static async Task<byte[]?> ReadBody(HttpRequest req)
{
    if (req.ContentLength > MaxBodyBytes) return null;
    using var ms = new MemoryStream();
    var buffer = new byte[16 * 1024];
    int n;
    while ((n = await req.Body.ReadAsync(buffer)) > 0)
    {
        if (ms.Length + n > MaxBodyBytes) return null;
        ms.Write(buffer, 0, n);
    }
    return ms.ToArray();
}

static T? Parse<T>(byte[] body) where T : class
{
    try { return JsonSerializer.Deserialize<T>(body, WireJson.Options); }
    catch (JsonException) { return null; }
}

// Verifies the request signature. With sigPubOverride (registration only) the key comes
// from the request body, which proves possession; otherwise from the account.
static async Task<(string? User, byte[] Body, IResult? Error)> Authenticate(
    HttpContext ctx, RelayStore store, ReplayGuard replay, Func<byte[], (string User, byte[] SigPub)?>? fromBody = null)
{
    var body = await ReadBody(ctx.Request);
    if (body is null) return (null, [], Error(413, "request too large"));
    var h = ctx.Request.Headers;
    string user = h[RelayAuth.UserHeader].ToString();
    if (!Usernames.IsValid(user)) return (null, body, Error(401, "missing or invalid user"));
    if (!long.TryParse(h[RelayAuth.TimestampHeader], out var ts)) return (null, body, Error(401, "missing timestamp"));
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    if (Math.Abs(now - ts) > RelayAuth.MaxSkew.TotalMilliseconds) return (null, body, Error(401, "stale timestamp"));
    byte[] sig;
    try { sig = Convert.FromBase64String(h[RelayAuth.SignatureHeader].ToString()); }
    catch (FormatException) { return (null, body, Error(401, "bad signature encoding")); }

    byte[]? sigPub;
    if (fromBody is not null)
    {
        if (fromBody(body) is not { } claimed || claimed.User != user) return (null, body, Error(400, "malformed registration"));
        sigPub = claimed.SigPub;
    }
    else
    {
        sigPub = store.GetIdentity(user)?.IdentitySigPub;
        if (sigPub is null) return (null, body, Error(401, "unknown user"));
    }

    var req = ctx.Request;
    var canonical = RelayAuth.Canonical(req.Method, $"{req.PathBase}{req.Path}{req.QueryString}", user, ts, body);
    bool valid;
    try { valid = sig.Length == 64 && sigPub.Length == 32 && Primitives.Verify(Primitives.SigPubFromBytes(sigPub), sig, canonical); }
    catch (FormatException) { valid = false; }
    if (!valid) return (null, body, Error(401, "bad signature"));
    if (!replay.TryUse(sig, ts)) return (null, body, Error(401, "replayed request"));
    return (user, body, null);
}

app.MapPost("/v1/accounts", async (HttpContext ctx, RelayStore store, ReplayGuard replay) =>
{
    RegisterRequest? reg = null;
    var (user, _, error) = await Authenticate(ctx, store, replay, body =>
    {
        reg = Parse<RegisterRequest>(body);
        return reg is { IdentitySigPub.Length: 32, IdentityDhPub.Length: 32 } && Usernames.IsValid(reg.Username)
            ? (reg.Username, reg.IdentitySigPub)
            : null;
    });
    if (error is not null) return error;
    return store.Register(reg!) switch
    {
        RegisterOutcome.Conflict => Error(409, "username is registered to a different identity key"),
        var outcome => Json(new { created = outcome == RegisterOutcome.Created }),
    };
});

app.MapPut("/v1/keys", async (HttpContext ctx, RelayStore store, ReplayGuard replay) =>
{
    var (user, body, error) = await Authenticate(ctx, store, replay);
    if (error is not null) return error;
    var req = Parse<UploadKeysRequest>(body);
    if (req is null || req.OneTimePrekeys is null || req.OneTimePrekeys.Count > MaxPrekeysPerUpload || req.OneTimePrekeys.Any(k => k.Pub?.Length != 32))
        return Error(400, "malformed key upload");
    if (req.SignedPrekey is { } spk)
    {
        var identity = store.GetIdentity(user!)!;
        if (spk.Pub is null || spk.Sig is null || !X3dh.VerifyPrekey(identity.IdentitySigPub, spk.Id, spk.Pub, spk.Sig))
            return Error(400, "signed prekey signature does not verify");
    }
    return store.UploadKeys(user!, req) ? Json(new { ok = true }) : Error(400, $"at most {RelayStore.MaxOneTimePrekeys} one-time prekeys");
});

app.MapGet("/v1/keys/status", async (HttpContext ctx, RelayStore store, ReplayGuard replay) =>
{
    var (user, _, error) = await Authenticate(ctx, store, replay);
    return error ?? Json(store.PrekeyStatus(user!));
});

app.MapGet("/v1/users/{username}/identity", async (string username, HttpContext ctx, RelayStore store, ReplayGuard replay) =>
{
    var (_, _, error) = await Authenticate(ctx, store, replay);
    if (error is not null) return error;
    return store.GetIdentity(username) is { } identity ? Json(identity) : Error(404, "unknown user");
});

app.MapGet("/v1/users/{username}/bundle", async (string username, HttpContext ctx, RelayStore store, ReplayGuard replay) =>
{
    var (_, _, error) = await Authenticate(ctx, store, replay);
    if (error is not null) return error;
    return store.TakeBundle(username) is { } bundle ? Json(bundle) : Error(404, "unknown user or no prekeys");
});

app.MapPost("/v1/users/{username}/messages", async (string username, HttpContext ctx, RelayStore store, ReplayGuard replay, InboxNotifier notifier) =>
{
    var (user, body, error) = await Authenticate(ctx, store, replay);
    if (error is not null) return error;
    if (username == user) return Error(400, "cannot message yourself");
    if (store.GetIdentity(username) is null) return Error(404, "unknown user");
    var env = Parse<EnvelopeDto>(body);
    if (env is null || env.Header?.Length != Header.Length || env.Ciphertext is null || env.Ciphertext.Length > MaxCiphertextBytes)
        return Error(400, "malformed envelope");
    if (env.Initial is { } i && (i.IdentitySigPub?.Length != 32 || i.IdentityDhPub?.Length != 32 || i.EphemeralPub?.Length != 32))
        return Error(400, "malformed initial message");
    var id = store.Enqueue(username, user!, JsonSerializer.Serialize(env, WireJson.Options));
    if (id is null) return Error(429, "recipient inbox is full");
    notifier.Notify(username);
    return Json(new { id });
});

app.MapGet("/v1/messages", async (HttpContext ctx, RelayStore store, ReplayGuard replay) =>
{
    var (user, _, error) = await Authenticate(ctx, store, replay);
    if (error is not null) return error;
    var items = store.Fetch(user!)
        .Select(m => new InboxItem(m.Id, m.Sender, m.ReceivedAt, JsonSerializer.Deserialize<EnvelopeDto>(m.Envelope, WireJson.Options)!))
        .ToList();
    return Json(new InboxResponse(items));
});

// Long poll: answers as soon as the caller has mail newer than inbox id `after`, or after
// `timeout` seconds (max 25). The answer says only whether such mail is waiting; the client
// then fetches it with GET /v1/messages. `after` lets a client ignore mail it is holding.
app.MapGet("/v1/messages/wait", async (HttpContext ctx, RelayStore store, ReplayGuard replay, InboxNotifier notifier) =>
{
    var (user, _, error) = await Authenticate(ctx, store, replay);
    if (error is not null) return error;
    var seconds = int.TryParse(ctx.Request.Query["timeout"], out var t) ? Math.Clamp(t, 0, 25) : 20;
    var after = long.TryParse(ctx.Request.Query["after"], out var a) ? a : 0;
    var signal = notifier.NextAsync(user!); // register before checking, so nothing slips through
    if (!store.HasMessagesAfter(user!, after) && seconds > 0)
    {
        try { await signal.WaitAsync(TimeSpan.FromSeconds(seconds), ctx.RequestAborted); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { return Results.Empty; }
    }
    return Json(new WaitResponse(store.HasMessagesAfter(user!, after)));
});

app.MapPost("/v1/messages/ack", async (HttpContext ctx, RelayStore store, ReplayGuard replay) =>
{
    var (user, body, error) = await Authenticate(ctx, store, replay);
    if (error is not null) return error;
    if (Parse<AckRequest>(body) is not { Ids: not null } ack || ack.Ids.Count > 1000) return Error(400, "malformed ack");
    store.Ack(user!, ack.Ids);
    return Json(new { ok = true });
});

app.Run();

public partial class Program; // exposed for WebApplicationFactory in tests
