// In-memory two-party E2EE demonstration through a running relay.
//
// Run: dotnet run --project src/E2ee.Client -- --server http://127.0.0.1:8000
// This demonstration does not persist keys or support independent chat terminals.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using E2ee;
using P = E2ee.Primitives;

var server = "http://127.0.0.1:8000";
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--server") server = args[i + 1];

static string B64(byte[] x) => Convert.ToBase64String(x);
static byte[] Un64(JsonNode? x) => Convert.FromBase64String((string)x!);
static uint? OptId(JsonNode? x) => x is null ? null : (uint)x;

using var http = new HttpClient { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(10) };

async Task<JsonObject> Post(string path, JsonObject data)
{
    var resp = await http.PostAsJsonAsync(path, new JsonObject { ["data"] = data });
    resp.EnsureSuccessStatusCode();
    return (await resp.Content.ReadFromJsonAsync<JsonObject>())!;
}

async Task<JsonObject> Get(string path)
{
    var resp = await http.GetAsync(path);
    resp.EnsureSuccessStatusCode();
    return (await resp.Content.ReadFromJsonAsync<JsonObject>())!;
}

var alice = X3dh.GenerateIdentity();
var bob = X3dh.GenerateIdentity();
var (store, bundles) = X3dh.GeneratePrekeys(bob, 3);
var bundle = bundles[0];

// Bob publishes his public prekey bundle.
var opkList = new JsonArray();
foreach (var (id, key) in store.OneTimePrekeys)
    opkList.Add(new JsonObject { ["id"] = id, ["pub"] = B64(P.DhPubBytes(key)) });
await Post("/users/bob/bundle", new JsonObject
{
    ["identity_sig_pub"] = B64(bundle.IdentitySigPub),
    ["identity_dh_pub"] = B64(bundle.IdentityDhPub),
    ["signed_prekey_id"] = bundle.SignedPrekeyId,
    ["signed_prekey_pub"] = B64(bundle.SignedPrekeyPub),
    ["signed_prekey_sig"] = B64(bundle.SignedPrekeySig),
    ["opks"] = opkList,
});

// Alice fetches it and runs X3DH.
var data = (await Get("/users/bob/bundle"))["data"]!;
var opk = data["opk"];
var fetched = new PrekeyBundle
{
    IdentitySigPub = Un64(data["identity_sig_pub"]),
    IdentityDhPub = Un64(data["identity_dh_pub"]),
    SignedPrekeyId = (uint)data["signed_prekey_id"]!,
    SignedPrekeyPub = Un64(data["signed_prekey_pub"]),
    SignedPrekeySig = Un64(data["signed_prekey_sig"]),
    OneTimePrekeyId = OptId(opk?["id"]),
    OneTimePrekeyPub = opk is null ? null : Un64(opk["pub"]),
};
var (skA, ad, ek) = X3dh.Initiate(alice, fetched);
var ra = Ratchet.InitAlice(skA, fetched.SignedPrekeyPub);
var (header, ct) = Ratchet.Encrypt(ra, "Hello Bob! This is encrypted."u8.ToArray(), ad);
await Post("/users/bob/messages", new JsonObject
{
    ["initial"] = new JsonObject
    {
        ["identity_sig_pub"] = B64(P.SigPubBytes(alice.SigPriv)),
        ["identity_dh_pub"] = B64(P.DhPubBytes(alice.DhPriv)),
        ["ephemeral_pub"] = B64(P.DhPubBytes(ek)),
        ["signed_prekey_id"] = fetched.SignedPrekeyId,
        ["one_time_prekey_id"] = fetched.OneTimePrekeyId,
    },
    ["header"] = B64(header.Encode()),
    ["ciphertext"] = B64(ct),
});

// Bob drains his inbox, completes X3DH, and decrypts.
var envelope = (await Get("/users/bob/messages"))["messages"]![0]!;
var init = envelope["initial"]!;
var receivedFirst = new InitialMessage(
    Un64(init["identity_sig_pub"]), Un64(init["identity_dh_pub"]), Un64(init["ephemeral_pub"]),
    (uint)init["signed_prekey_id"]!, OptId(init["one_time_prekey_id"]), []);
var (skB, adB) = X3dh.Respond(store, receivedFirst);
var rb = Ratchet.InitBob(skB, store.SignedPrekeys[receivedFirst.SignedPrekeyId]);
var text = Ratchet.Decrypt(rb, Header.Decode(Un64(envelope["header"])), Un64(envelope["ciphertext"]), adB);
X3dh.ConsumeOneTimePrekey(store, receivedFirst.OneTimePrekeyId); // only after the first message authenticated
Console.WriteLine($"Bob decrypted: {Encoding.UTF8.GetString(text)}");

// Bob replies; Alice decrypts.
var (header2, ct2) = Ratchet.Encrypt(rb, "Hi Alice, I can read you!"u8.ToArray(), adB);
await Post("/users/alice/messages", new JsonObject { ["header"] = B64(header2.Encode()), ["ciphertext"] = B64(ct2) });
var reply = (await Get("/users/alice/messages"))["messages"]![0]!;
var replyText = Ratchet.Decrypt(ra, Header.Decode(Un64(reply["header"])), Un64(reply["ciphertext"]), ad);
Console.WriteLine($"Alice decrypted: {Encoding.UTF8.GetString(replyText)}");
Console.WriteLine("SUCCESS: relay transported ciphertext; both peers decrypted.");
