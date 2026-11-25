using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using E2ee.Wire;
using NSec.Cryptography;

namespace E2ee.Messaging;

public class RelayException(HttpStatusCode? status, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Null when the relay could not be reached at all.</summary>
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>HTTP client for E2ee.Server. Signs every request with the identity key.</summary>
public sealed class RelayClient(HttpClient http)
{
    private string? _user;
    private Key? _sigKey;

    public Uri BaseAddress => http.BaseAddress!;

    public void SetIdentity(string user, Key sigKey) => (_user, _sigKey) = (user, sigKey);

    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (_user is null || _sigKey is null) throw new InvalidOperationException("relay identity not set");
        var bytes = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), WireJson.Options);
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pathAndQuery = new Uri(http.BaseAddress!, path).PathAndQuery;
        var sig = Primitives.Sign(_sigKey, RelayAuth.Canonical(method.Method, pathAndQuery, _user, ts, bytes));
        req.Headers.Add(RelayAuth.UserHeader, _user);
        req.Headers.Add(RelayAuth.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        req.Headers.Add(RelayAuth.SignatureHeader, Convert.ToBase64String(sig));

        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new RelayException(null, $"relay unreachable: {e.Message}", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new RelayException(null, "relay timed out", e); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                string message;
                try { message = (await resp.Content.ReadFromJsonAsync<ErrorResponse>(WireJson.Options, ct))?.Error ?? resp.ReasonPhrase ?? ""; }
                catch (JsonException) { message = resp.ReasonPhrase ?? ""; }
                throw new RelayException(resp.StatusCode, message);
            }
            return (await resp.Content.ReadFromJsonAsync<T>(WireJson.Options, ct))!;
        }
    }

    private sealed record Ok;

    public Task RegisterAsync(RegisterRequest req, CancellationToken ct = default) => Send<Ok>(HttpMethod.Post, "v1/accounts", req, ct);
    public Task UploadKeysAsync(UploadKeysRequest req, CancellationToken ct = default) => Send<Ok>(HttpMethod.Put, "v1/keys", req, ct);
    public Task<PrekeyStatusResponse> PrekeyStatusAsync(CancellationToken ct = default) => Send<PrekeyStatusResponse>(HttpMethod.Get, "v1/keys/status", null, ct);
    public Task<IdentityResponse> GetIdentityAsync(string user, CancellationToken ct = default) => Send<IdentityResponse>(HttpMethod.Get, $"v1/users/{user}/identity", null, ct);
    public Task<BundleResponse> TakeBundleAsync(string user, CancellationToken ct = default) => Send<BundleResponse>(HttpMethod.Get, $"v1/users/{user}/bundle", null, ct);
    public Task SendAsync(string recipient, EnvelopeDto env, CancellationToken ct = default) => Send<Ok>(HttpMethod.Post, $"v1/users/{recipient}/messages", env, ct);
    public Task<InboxResponse> FetchAsync(CancellationToken ct = default) => Send<InboxResponse>(HttpMethod.Get, "v1/messages", null, ct);
    public Task AckAsync(List<long> ids, CancellationToken ct = default) => Send<Ok>(HttpMethod.Post, "v1/messages/ack", new AckRequest(ids), ct);
}
