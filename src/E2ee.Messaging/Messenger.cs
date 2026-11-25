// The session layer: one Messenger per local profile. It owns the identity, prekeys,
// contacts, Double Ratchet sessions, and message history, and talks to the relay.
// The desktop app and the CLI both sit on top of this class; neither touches crypto.

using System.Text;
using System.Text.Json;
using E2ee.Wire;
using NSec.Cryptography;
using P = E2ee.Primitives;

namespace E2ee.Messaging;

public sealed record MessengerOptions
{
    /// <summary>Profile directory holding the database. One process at a time.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Relay URL. Required to register; afterwards it is remembered.</summary>
    public Uri? Server { get; init; }

    /// <summary>Overrides the HTTP client (tests). Its BaseAddress is the relay URL.</summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Overrides where the master key lives. Default: <see cref="MasterKeyStore.ForCurrentPlatform"/>.</summary>
    public IMasterKeyStore? KeyStore { get; init; }

    public PrekeyPolicy Prekeys { get; init; } = new();
}

public sealed record PrekeyPolicy
{
    public int InitialOneTimePrekeys { get; init; } = 100;
    public int RefillBelow { get; init; } = 25;
    public int RefillBatch { get; init; } = 75;
    public TimeSpan SignedPrekeyRotation { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan OldSignedPrekeyRetention { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record SyncResult(IReadOnlyList<StoredMessage> Received, IReadOnlyList<string> Problems);

public class MessengerException(string message) : Exception(message);
public sealed class ProfileInUseException(string dir) : MessengerException($"Profile {dir} is already open in another process.");
public sealed class UsernameTakenException(string user) : MessengerException($"The username '{user}' is already registered to someone else.");
public sealed class UserNotFoundException(string user) : MessengerException($"No user named '{user}' is registered on this relay.");
public sealed class IdentityChangedException(string user) : MessengerException(
    $"{user}'s identity key does not match the one you saw before. Someone may be impersonating them, or they reinstalled. " +
    "Check with them out of band before trusting the new key.");

/// <summary>Plaintext inside every ratchet message. Leaves room for other types later.</summary>
internal sealed record MessageContent(string Type, string Body, long SentAt);

public sealed class Messenger : IDisposable
{
    private const string Spk = "spk", Opk = "opk";
    private const int MaxSessionsPerPeer = 5;

    private readonly LocalStore _store;
    private readonly RelayClient _relay;
    private readonly FileStream _lock;
    private readonly PrekeyPolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _ownsHttp;
    private readonly HttpClient _http;
    private IdentityKeys? _identity;
    private DateTimeOffset _lastPrekeyCheck = DateTimeOffset.MinValue;

    public string? Username { get; private set; }
    public bool IsRegistered => _store.GetMeta("registered") == "1";
    public Uri Server => _relay.BaseAddress;

    private Messenger(LocalStore store, RelayClient relay, HttpClient http, bool ownsHttp, FileStream lockFile, PrekeyPolicy policy)
    {
        (_store, _relay, _http, _ownsHttp, _lock, _policy) = (store, relay, http, ownsHttp, lockFile, policy);
    }

    public static Messenger Open(MessengerOptions o)
    {
        Directory.CreateDirectory(o.DataDirectory);
        FileStream lockFile;
        try { lockFile = new FileStream(Path.Combine(o.DataDirectory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new ProfileInUseException(o.DataDirectory); }

        LocalStore? store = null;
        try
        {
            var masterKey = (o.KeyStore ?? MasterKeyStore.ForCurrentPlatform(o.DataDirectory)).GetOrCreate();
            store = new LocalStore(Path.Combine(o.DataDirectory, "messenger.db"), masterKey);

            var saved = store.GetMeta("server") is { } s ? new Uri(s) : null;
            var requested = o.HttpClient?.BaseAddress ?? o.Server;
            if (saved is not null && requested is not null && Normalize(saved) != Normalize(requested))
                throw new MessengerException($"This profile is registered on {saved}, not {requested}.");
            var server = requested ?? saved ?? throw new MessengerException("No relay URL given and none saved in this profile.");
            var http = o.HttpClient ?? new HttpClient { BaseAddress = Normalize(server), Timeout = TimeSpan.FromSeconds(15) };

            var m = new Messenger(store, new RelayClient(http), http, o.HttpClient is null, lockFile, o.Prekeys);
            m.LoadIdentity();
            return m;
        }
        catch
        {
            store?.Dispose();
            lockFile.Dispose();
            throw;
        }
    }

    private static Uri Normalize(Uri u) => u.AbsoluteUri.EndsWith('/') ? u : new Uri(u.AbsoluteUri + "/");

    private void LoadIdentity()
    {
        byte[]? sig, dh;
        try { (sig, dh) = (_store.GetSecret("identity.sig"), _store.GetSecret("identity.dh")); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new MessengerException("Cannot decrypt this profile: the master key does not match (was the keychain entry removed?).");
        }
        if (sig is null || dh is null) return;
        _identity = new IdentityKeys(P.SigPrivFromBytes(sig), P.DhPrivFromBytes(dh));
        Username = _store.GetMeta("username");
        if (Username is not null) _relay.SetIdentity(Username, _identity.SigPriv);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (_ownsHttp) _http.Dispose();
        _lock.Dispose();
        _gate.Dispose();
    }

    private async Task<T> Locked<T>(Func<Task<T>> body)
    {
        await _gate.WaitAsync();
        try { return await body(); }
        finally { _gate.Release(); }
    }

    private void RequireRegistered()
    {
        if (!IsRegistered || _identity is null || Username is null) throw new MessengerException("Register first.");
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    // ================================================================ registration and prekeys

    /// <summary>Creates the identity (once), claims the username on the relay, and uploads prekeys.
    /// Safe to call again after a failure.</summary>
    public Task RegisterAsync(string username, CancellationToken ct = default) => Locked(async () =>
    {
        if (IsRegistered) throw new MessengerException($"This profile is already registered as {Username}.");
        if (!Usernames.IsValid(username)) throw new MessengerException("Usernames are 3-32 characters: lowercase letters, digits, and underscores.");

        if (_identity is null)
        {
            var id = X3dh.GenerateIdentity();
            using var tx = _store.Begin();
            _store.SetSecret("identity.sig", P.SigPrivBytes(id.SigPriv));
            _store.SetSecret("identity.dh", P.DhPrivBytes(id.DhPriv));
            tx.Commit();
            _identity = id;
        }
        _relay.SetIdentity(username, _identity.SigPriv);
        try
        {
            await _relay.RegisterAsync(new RegisterRequest(username, P.SigPubBytes(_identity.SigPriv), P.DhPubBytes(_identity.DhPriv)), ct);
        }
        catch (RelayException e) when (e.Status == System.Net.HttpStatusCode.Conflict)
        {
            throw new UsernameTakenException(username);
        }
        await UploadSignedPrekeyAsync(ct);
        await UploadOneTimePrekeysAsync(_policy.InitialOneTimePrekeys, ct);

        using (var tx = _store.Begin())
        {
            _store.SetMeta("username", username);
            _store.SetMeta("server", Server.AbsoluteUri);
            _store.SetMeta("registered", "1");
            tx.Commit();
        }
        Username = username;
        _lastPrekeyCheck = Now;
        return true;
    });

    private uint NextId(string counter)
    {
        var next = uint.Parse(_store.GetMeta(counter) ?? "1");
        _store.SetMeta(counter, (next + 1).ToString());
        return next;
    }

    private async Task UploadSignedPrekeyAsync(CancellationToken ct)
    {
        var key = P.DhGenerate();
        uint id;
        using (var tx = _store.Begin())
        {
            id = NextId("next_spk_id");
            _store.PutPrekey(Spk, id, P.DhPrivBytes(key), Now);
            tx.Commit();
        }
        var pub = P.DhPubBytes(key);
        await _relay.UploadKeysAsync(new UploadKeysRequest(new SignedPrekeyDto(id, pub, X3dh.SignPrekey(_identity!, id, pub)), []), ct);
        using (var tx = _store.Begin())
        {
            _store.SetMeta("current_spk_id", id.ToString());
            tx.Commit();
        }
    }

    private async Task UploadOneTimePrekeysAsync(int count, CancellationToken ct)
    {
        const int Chunk = 100;
        for (var done = 0; done < count; done += Chunk)
        {
            var batch = new List<OneTimePrekeyDto>();
            using (var tx = _store.Begin())
            {
                for (var i = 0; i < Math.Min(Chunk, count - done); i++)
                {
                    var key = P.DhGenerate();
                    var id = NextId("next_opk_id");
                    _store.PutPrekey(Opk, id, P.DhPrivBytes(key), Now);
                    batch.Add(new OneTimePrekeyDto(id, P.DhPubBytes(key)));
                }
                tx.Commit();
            }
            await _relay.UploadKeysAsync(new UploadKeysRequest(null, batch), ct);
        }
    }

    /// <summary>Refills one-time prekeys on the relay and rotates the signed prekey when due.</summary>
    private async Task MaintainPrekeysAsync(bool force, CancellationToken ct)
    {
        if (!force && Now - _lastPrekeyCheck < _policy.CheckInterval) return;
        var status = await _relay.PrekeyStatusAsync(ct);
        if (status.OneTimePrekeys < _policy.RefillBelow) await UploadOneTimePrekeysAsync(_policy.RefillBatch, ct);

        var current = _store.GetMeta("current_spk_id") is { } c ? uint.Parse(c) : (uint?)null;
        var spks = _store.ListPrekeys(Spk);
        var currentCreated = spks.FirstOrDefault(k => k.Id == current).CreatedAt;
        if (current is null || status.SignedPrekeyId != current || Now - currentCreated > _policy.SignedPrekeyRotation)
            await UploadSignedPrekeyAsync(ct);

        // Old signed prekeys stay around a while so first messages still in flight can be read.
        current = uint.Parse(_store.GetMeta("current_spk_id")!);
        using (var tx = _store.Begin())
        {
            foreach (var (id, created) in spks)
                if (id != current && Now - created > _policy.OldSignedPrekeyRetention) _store.DeletePrekey(Spk, id);
            tx.Commit();
        }
        _lastPrekeyCheck = Now;
    }

    // ================================================================ contacts

    public IReadOnlyList<Contact> Contacts() => _store.Contacts();

    public Contact? GetContact(string username) => _store.GetContact(username);

    /// <summary>Looks the user up on the relay and remembers their identity key (trust on first use).</summary>
    public Task<Contact> AddContactAsync(string username, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        if (username == Username) throw new MessengerException("That's you.");
        var identity = await GetIdentityOrThrow(username, ct);
        using var tx = _store.Begin();
        var contact = Trust(username, identity.IdentitySigPub, identity.IdentityDhPub);
        tx.Commit();
        return contact;
    });

    /// <summary>Accepts the identity key the relay currently has for the user, replacing the
    /// remembered one and dropping existing sessions. Only after checking with them.</summary>
    public Task<Contact> TrustNewIdentityAsync(string username, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        var identity = await GetIdentityOrThrow(username, ct);
        var contact = new Contact(username, identity.IdentitySigPub, identity.IdentityDhPub, Now);
        using var tx = _store.Begin();
        _store.PutContact(contact);
        _store.DeleteSessions(username);
        tx.Commit();
        return contact;
    });

    private async Task<IdentityResponse> GetIdentityOrThrow(string username, CancellationToken ct)
    {
        try { return await _relay.GetIdentityAsync(username, ct); }
        catch (RelayException e) when (e.Status == System.Net.HttpStatusCode.NotFound) { throw new UserNotFoundException(username); }
    }

    /// <summary>Trust on first use. Must run inside a transaction.</summary>
    private Contact Trust(string username, byte[] sigPub, byte[] dhPub)
    {
        if (_store.GetContact(username) is { } known)
        {
            if (!known.IdentitySigPub.AsSpan().SequenceEqual(sigPub) || !known.IdentityDhPub.AsSpan().SequenceEqual(dhPub))
                throw new IdentityChangedException(username);
            return known;
        }
        var contact = new Contact(username, sigPub, dhPub, Now);
        _store.PutContact(contact);
        return contact;
    }

    // ================================================================ history

    public IReadOnlyList<StoredMessage> History(string peer, int limit = 50) => _store.History(peer, limit);

    // ================================================================ sending

    /// <summary>
    /// Encrypts and queues a message, then tries to deliver it. If the relay is down the
    /// message stays Pending and the same ciphertext is retried on the next sync.
    /// </summary>
    public Task<StoredMessage> SendAsync(string peer, string text, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        if (peer == Username) throw new MessengerException("You can't message yourself.");

        // A new session needs a bundle from the relay; fetch it before opening a transaction.
        BundleResponse? bundle = null;
        if (_store.Sessions(peer).Count == 0)
        {
            try { bundle = await _relay.TakeBundleAsync(peer, ct); }
            catch (RelayException e) when (e.Status == System.Net.HttpStatusCode.NotFound) { throw new UserNotFoundException(peer); }
        }

        long messageId;
        EnvelopeDto env;
        using (var tx = _store.Begin())
        {
            var (sid, rec) = bundle is null ? _store.Sessions(peer)[0] : StartSession(peer, bundle);
            var state = RatchetState.FromDto(rec.State);
            var sentAt = Now;
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(new MessageContent("text", text, sentAt.ToUnixTimeMilliseconds()), WireJson.Options);
            var (header, ct2) = Ratchet.Encrypt(state, plaintext, rec.Ad);
            env = new EnvelopeDto(rec.PendingInitial, header.Encode(), ct2);

            _store.PutSession(peer, sid, rec with { State = state.ToDto() }, Now);
            messageId = _store.InsertMessage(peer, true, text, sentAt, MessageStatus.Pending);
            _store.PutOutbox(messageId, peer, env);
            tx.Commit();
        }

        try { await DeliverAsync(messageId, peer, env, ct); }
        catch (RelayException e) when (e.Status is null) { /* stays pending; retried on sync */ }
        return _store.GetMessage(messageId);
    });

    /// <summary>X3DH as the initiator. Runs inside the caller's transaction.</summary>
    private (string Sid, SessionRecord Rec) StartSession(string peer, BundleResponse b)
    {
        Trust(peer, b.IdentitySigPub, b.IdentityDhPub);
        var bundle = new PrekeyBundle
        {
            IdentitySigPub = b.IdentitySigPub,
            IdentityDhPub = b.IdentityDhPub,
            SignedPrekeyId = b.SignedPrekey.Id,
            SignedPrekeyPub = b.SignedPrekey.Pub,
            SignedPrekeySig = b.SignedPrekey.Sig,
            OneTimePrekeyId = b.OneTimePrekey?.Id,
            OneTimePrekeyPub = b.OneTimePrekey?.Pub,
        };
        var (sk, ad, ek) = X3dh.Initiate(_identity!, bundle);
        var state = Ratchet.InitAlice(sk, bundle.SignedPrekeyPub);
        var ephemeralPub = P.DhPubBytes(ek);
        ek.Dispose();
        var initial = new InitialDto(P.SigPubBytes(_identity!.SigPriv), P.DhPubBytes(_identity.DhPriv), ephemeralPub, bundle.SignedPrekeyId, bundle.OneTimePrekeyId);
        var sid = Convert.ToHexString(ephemeralPub);
        var rec = new SessionRecord(state.ToDto(), ad, initial);
        _store.PutSession(peer, sid, rec, Now);
        _store.PruneSessions(peer, MaxSessionsPerPeer);
        return (sid, rec);
    }

    private async Task DeliverAsync(long messageId, string peer, EnvelopeDto env, CancellationToken ct)
    {
        try
        {
            await _relay.SendAsync(peer, env, ct);
        }
        catch (RelayException e) when (e.Status is not null)
        {
            // The relay refused it for good (unknown user, malformed, inbox full): give up on it.
            using var failTx = _store.Begin();
            _store.DeleteOutbox(messageId);
            _store.SetMessageStatus(messageId, MessageStatus.Failed);
            failTx.Commit();
            throw;
        }
        using var tx = _store.Begin();
        _store.DeleteOutbox(messageId);
        _store.SetMessageStatus(messageId, MessageStatus.Sent);
        tx.Commit();
    }

    // ================================================================ receiving

    /// <summary>
    /// Retries queued messages, fetches and decrypts the inbox, acknowledges what was
    /// handled, and keeps prekeys topped up. Each inbox item is decrypted and stored in
    /// one transaction before it is acknowledged, so a crash at any point loses nothing
    /// and never applies a message twice.
    /// </summary>
    public Task<SyncResult> SyncAsync(CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        var problems = new List<string>();

        foreach (var (messageId, peer, env) in _store.Outbox())
        {
            try { await DeliverAsync(messageId, peer, env, ct); }
            catch (RelayException e) when (e.Status is null) { break; }
            catch (RelayException e) { problems.Add($"Could not deliver a message to {peer}: {e.Message}"); }
        }

        var received = new List<StoredMessage>();
        var consumedPrekey = false;
        while (true)
        {
            var inbox = await _relay.FetchAsync(ct);
            if (inbox.Messages.Count == 0) break;
            var handled = new List<long>();
            foreach (var item in inbox.Messages)
            {
                if (!_store.IsProcessed(item.Id))
                {
                    var (message, problem, usedPrekey) = await ProcessAsync(item, ct);
                    if (message is not null) received.Add(message);
                    if (problem is not null) problems.Add(problem);
                    consumedPrekey |= usedPrekey;
                }
                handled.Add(item.Id);
            }
            await _relay.AckAsync(handled, ct);
            if (inbox.Messages.Count < 100) break;
        }

        await MaintainPrekeysAsync(consumedPrekey, ct);
        return new SyncResult(received, problems);
    });

    private async Task<(StoredMessage? Message, string? Problem, bool UsedPrekey)> ProcessAsync(InboxItem item, CancellationToken ct)
    {
        var sender = item.Sender;
        // A first message from a stranger: confirm the identity in it is the one the relay
        // has registered for that username, so nobody can speak under someone else's name.
        IdentityResponse? relayIdentity = null;
        if (item.Envelope.Initial is { } first && _store.GetContact(sender) is null
            && _store.GetSession(sender, Convert.ToHexString(first.EphemeralPub)) is null)
        {
            try { relayIdentity = await _relay.GetIdentityAsync(sender, ct); }
            catch (RelayException e) when (e.Status is not null) { return Drop(item, $"Dropped a message from {sender}: {e.Message}"); }
        }

        using var tx = _store.Begin();
        try
        {
            var (plaintext, usedPrekey) = Decrypt(item, relayIdentity);
            var content = JsonSerializer.Deserialize<MessageContent>(plaintext, WireJson.Options)
                ?? throw new ProtocolException("empty message");
            var sentAt = DateTimeOffset.FromUnixTimeMilliseconds(content.SentAt);
            var id = _store.InsertMessage(sender, false, content.Body, sentAt, MessageStatus.Received);
            _store.MarkProcessed(item.Id, "ok");
            tx.Commit();
            return (_store.GetMessage(id), null, usedPrekey);
        }
        catch (Exception e) when (e is ProtocolException or IdentityChangedException or JsonException
                                      or System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
        {
            tx.Dispose();
            var reason = e is IdentityChangedException ? e.Message : $"it could not be decrypted ({e.Message})";
            return Drop(item, $"Dropped a message from {sender}: {reason}");
        }
    }

    private (StoredMessage?, string?, bool) Drop(InboxItem item, string problem)
    {
        using var tx = _store.Begin();
        _store.MarkProcessed(item.Id, "dropped");
        tx.Commit();
        return (null, problem, false);
    }

    /// <summary>Runs inside the caller's transaction and writes the updated session.</summary>
    private (byte[] Plaintext, bool UsedPrekey) Decrypt(InboxItem item, IdentityResponse? relayIdentity)
    {
        var sender = item.Sender;
        var env = item.Envelope;
        var header = Header.Decode(env.Header);

        if (env.Initial is { } init)
        {
            var sid = Convert.ToHexString(init.EphemeralPub);
            if (_store.GetSession(sender, sid) is { } existing)
                return (DecryptWith(sender, sid, existing, header, env.Ciphertext), false);

            if (relayIdentity is not null &&
                (!relayIdentity.IdentitySigPub.AsSpan().SequenceEqual(init.IdentitySigPub) || !relayIdentity.IdentityDhPub.AsSpan().SequenceEqual(init.IdentityDhPub)))
                throw new IdentityChangedException(sender);
            Trust(sender, init.IdentitySigPub, init.IdentityDhPub);

            // X3DH as the responder, using only the prekeys this message names.
            var prekeys = new LocalPrekeyStore(_identity!);
            var spk = _store.GetPrekey(Spk, init.SignedPrekeyId) ?? throw new ProtocolException("unknown or expired signed prekey");
            prekeys.SignedPrekeys[init.SignedPrekeyId] = P.DhPrivFromBytes(spk);
            if (init.OneTimePrekeyId is { } opkId && _store.GetPrekey(Opk, opkId) is { } opk)
                prekeys.OneTimePrekeys[opkId] = P.DhPrivFromBytes(opk);
            var msg = new InitialMessage(init.IdentitySigPub, init.IdentityDhPub, init.EphemeralPub, init.SignedPrekeyId, init.OneTimePrekeyId, []);
            var (sk, ad) = X3dh.Respond(prekeys, msg);
            var state = Ratchet.InitBob(sk, prekeys.SignedPrekeys[init.SignedPrekeyId]);
            var plaintext = Ratchet.Decrypt(state, header, env.Ciphertext, ad);

            // Authenticated: only now delete the one-time prekey and save the session.
            if (init.OneTimePrekeyId is { } used) _store.DeletePrekey(Opk, used);
            _store.PutSession(sender, sid, new SessionRecord(state.ToDto(), ad, null), Now);
            _store.PruneSessions(sender, MaxSessionsPerPeer);
            return (plaintext, true);
        }

        // An ongoing message: try each session with this peer, newest first. After both
        // sides start a session at the same moment there are two, and this converges on one.
        foreach (var (sid, rec) in _store.Sessions(sender))
        {
            try { return (DecryptWith(sender, sid, rec, header, env.Ciphertext), false); }
            catch (System.Security.Cryptography.CryptographicException) { }
            catch (ProtocolException) { }
        }
        throw new ProtocolException("no session with this sender can decrypt it (replayed, or from a lost session)");
    }

    private byte[] DecryptWith(string peer, string sid, SessionRecord rec, Header header, byte[] ciphertext)
    {
        var state = RatchetState.FromDto(rec.State);
        var plaintext = Ratchet.Decrypt(state, header, ciphertext, rec.Ad);
        // The peer has replied on this session, so stop resending the X3DH fields.
        _store.PutSession(peer, sid, new SessionRecord(state.ToDto(), rec.Ad, null), Now);
        return plaintext;
    }
}
