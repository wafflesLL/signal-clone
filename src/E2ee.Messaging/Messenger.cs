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

/// <summary>A chat in the conversation list: either a direct chat (Contact) or a group (Group).</summary>
public sealed record Conversation(Contact? Contact, Group? Group, StoredMessage? Last)
{
    public DateTimeOffset LastActivity => Last?.SentAt ?? Contact?.AddedAt ?? Group!.CreatedAt;
}

/// <summary>An identity key as the relay reports it, with the safety number it gives.</summary>
public sealed record IdentityCheck(string Username, byte[] IdentitySigPub, byte[] IdentityDhPub, string SafetyNumber, bool MatchesTrustedKey);

/// <param name="IdentityChanges">Contacts whose safety number changed during this sync.</param>
/// <param name="Updated">Messages already shown that changed: an attachment finished downloading, or someone reacted.</param>
public sealed record SyncResult(IReadOnlyList<StoredMessage> Received, IReadOnlyList<string> Problems, IReadOnlyList<string> IdentityChanges,
    IReadOnlyList<StoredMessage>? Updated = null)
{
    public IReadOnlyList<StoredMessage> Updated { get; init; } = Updated ?? [];
}

public class MessengerException(string message) : Exception(message);
public sealed class NoRelayConfiguredException() : MessengerException("No relay URL given and none saved in this profile. Register first.");
public sealed class ProfileInUseException(string dir) : MessengerException($"Profile {dir} is already open in another process.");
public sealed class UsernameTakenException(string user) : MessengerException($"The username '{user}' is already registered to someone else.");
/// <summary>Messaging someone before you're friends.</summary>
public sealed class NotFriendsException(string user, string message) : MessengerException(message)
{
    public string Username { get; } = user;
}

public sealed class UserNotFoundException(string user) : MessengerException($"No user named '{user}' is registered on this relay.");
public sealed class IdentityChangedException(string user) : MessengerException(
    $"{user}'s safety number changed: their identity key is not the one you trusted before. Someone may be " +
    "impersonating them, or they reinstalled. Compare safety numbers with them before trusting the new key.")
{
    public string Username { get; } = user;
}

/// <summary>
/// Plaintext inside every ratchet message. Type is "text", "reaction", "group_update" (the
/// group's name and full member list, sent when it is created or someone is added),
/// "group_leave", "contact_request", or "contact_accept".
/// Group is set for anything that belongs to a group chat.
/// </summary>
internal sealed record MessageContent(string Type, string Body, long SentAt, GroupContext? Group = null,
    AttachmentPointer? Attachment = null, ReactionContent? Reaction = null);

/// <summary>A reaction (or, with a null Emoji, taking one back) to the message <paramref name="Author"/> sent at <paramref name="SentAt"/>.</summary>
internal sealed record ReactionContent(string Author, long SentAt, string? Emoji);

/// <param name="Members">Everyone in the group, sender included. Only in group_update.</param>
internal sealed record GroupContext(string Id, string? Name = null, List<string>? Members = null);

public sealed class Messenger : IDisposable
{
    private const string Spk = "spk", Opk = "opk";
    private const int MaxSessionsPerPeer = 5;
    public const int MaxGroupSize = 50; // including you
    public const int MaxGroupNameLength = 64;

    /// <summary>
    /// Longest message (or caption) that can be sent, in characters. Keeps every encrypted
    /// message well under the relay's 128 KB limit, even with emoji and escaped characters.
    /// </summary>
    public const int MaxTextLength = 16_000;

    private static void CheckText(string text)
    {
        if (text.Length > MaxTextLength)
            throw new MessengerException($"That message is {text.Length:N0} characters; the limit is {MaxTextLength:N0}. Send it as a document instead.");
    }

    private readonly LocalStore _store;
    private readonly RelayClient _relay;
    private readonly FileStream _lock;
    private readonly PrekeyPolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _ownsHttp;
    private readonly HttpClient _http;
    private readonly string _dataDir;
    private IdentityKeys? _identity;
    private DateTimeOffset _lastPrekeyCheck = DateTimeOffset.MinValue;
    private long _lastSeenInboxId; // highest relay inbox id fetched; held messages stay below it

    public string? Username { get; private set; }
    public bool IsRegistered => _store.GetMeta("registered") == "1";
    public Uri Server => _relay.BaseAddress;

    private Messenger(LocalStore store, RelayClient relay, HttpClient http, bool ownsHttp, FileStream lockFile, PrekeyPolicy policy)
    {
        (_store, _relay, _http, _ownsHttp, _lock, _policy) = (store, relay, http, ownsHttp, lockFile, policy);
        _dataDir = Path.GetDirectoryName(lockFile.Name)!;
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
            var server = requested ?? saved ?? throw new NoRelayConfiguredException();
            // RelayClient sets a timeout per request (longer for attachments), so none here.
            var http = o.HttpClient ?? new HttpClient { BaseAddress = Normalize(server), Timeout = Timeout.InfiniteTimeSpan };

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

    /// <summary>
    /// Log out, second half: erases a closed profile from this computer: its keys,
    /// history, contacts, and media, plus the master key wherever it is kept.
    /// Call <see cref="CloseAccountAsync"/> first, then dispose the Messenger.
    /// </summary>
    public static void DeleteProfile(MessengerOptions o)
    {
        if (!Directory.Exists(o.DataDirectory)) return;
        // Taking the lock proves no Messenger (in this app or another) still has the profile open.
        using (var probe = TryLock(o.DataDirectory) ?? throw new ProfileInUseException(o.DataDirectory)) { }
        (o.KeyStore ?? MasterKeyStore.ForCurrentPlatform(o.DataDirectory)).Delete();
        Directory.Delete(o.DataDirectory, recursive: true);
    }

    private static FileStream? TryLock(string dir)
    {
        try { return new FileStream(Path.Combine(dir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
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

    /// <summary>Waits for an operation in progress to finish, then closes the profile.</summary>
    public void Dispose()
    {
        _gate.Wait();
        _store.Dispose();
        if (_ownsHttp) _http.Dispose();
        try { if (Directory.Exists(OpenFolder)) Directory.Delete(OpenFolder, recursive: true); } catch (IOException) { }
        _lock.Dispose();
        _gate.Dispose();
    }

    // Every operation runs one at a time, off the caller's thread. Running the body on the
    // thread pool means no continuation needs a UI thread, so a UI can never deadlock here.
    private async Task<T> Locked<T>(Func<Task<T>> body)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(body).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private Task<T> Locked<T>(Func<T> body) => Locked(() => Task.FromResult(body()));

    private T LockedSync<T>(Func<T> body)
    {
        _gate.Wait();
        try { return body(); }
        finally { _gate.Release(); }
    }

    private void RequireRegistered()
    {
        if (!IsRegistered || _identity is null || Username is null) throw new MessengerException("Register first.");
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    // ================================================================ registration and prekeys

    /// <summary>
    /// Log out, first half: closes the account on the relay, which frees the username and
    /// drops its prekeys and anything still queued for it. Then dispose this Messenger
    /// and call <see cref="DeleteProfile"/>. Contacts who later message the username
    /// see a safety-number change if someone else registers it.
    /// </summary>
    public Task CloseAccountAsync(CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        await _relay.DeleteAccountAsync(ct);
        _store.SetMeta("registered", "0");
        return true;
    });

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

    public IReadOnlyList<Contact> Contacts() => LockedSync<IReadOnlyList<Contact>>(_store.Contacts);

    public Contact? GetContact(string username) => LockedSync(() => _store.GetContact(username));

    /// <summary>Every direct chat and group with its latest message, most recent activity first.</summary>
    public Task<IReadOnlyList<Conversation>> ConversationsAsync() => Locked<IReadOnlyList<Conversation>>(() =>
        _store.Contacts()
            .Where(c => c.IsListed)
            .Select(c => new Conversation(c, null, _store.History(c.Username, 1).FirstOrDefault()))
            .Concat(_store.Groups().Select(g => new Conversation(null, g, _store.GroupHistory(g.Id, 1).FirstOrDefault())))
            .OrderByDescending(c => c.LastActivity)
            .ToList());

    /// <summary>
    /// Sends a contact request: looks the user up on the relay, remembers their identity key
    /// (trust on first use), and asks them to accept. Nothing else can be sent until they do.
    /// If they had already asked you, this accepts instead.
    /// </summary>
    public Task<Contact> AddContactAsync(string username, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        if (username == Username) throw new MessengerException("That's you.");
        var known = _store.GetContact(username);
        switch (known?.State)
        {
            case ContactState.Friend:
            case ContactState.RequestSent:
                return known;
            case ContactState.RequestReceived:
                return await AcceptAsync(known, ct);
        }
        if (known is null)
        {
            var identity = await GetIdentityOrThrow(username, ct);
            using var tx = _store.Begin();
            Trust(username, identity.IdentitySigPub, identity.IdentityDhPub);
            tx.Commit();
        }
        if (_store.GetContact(username) is { IdentityChanged: true }) throw new IdentityChangedException(username);
        await SendControlAsync(username, "contact_request", () => SetState(username, ContactState.RequestSent), ct);
        return _store.GetContact(username)!;
    });

    /// <summary>Accepts someone's contact request. You can message each other from now on.</summary>
    public Task<Contact> AcceptRequestAsync(string username, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        var c = _store.GetContact(username);
        if (c is not { State: ContactState.RequestReceived }) throw new MessengerException($"{username} hasn't sent you a request.");
        return await AcceptAsync(c, ct);
    });

    private async Task<Contact> AcceptAsync(Contact c, CancellationToken ct)
    {
        if (c.IdentityChanged) throw new IdentityChangedException(c.Username);
        await SendControlAsync(c.Username, "contact_accept", () =>
        {
            SetState(c.Username, ContactState.Friend);
            _store.InsertMessage(c.Username, true, $"You accepted {c.Username}'s request. Say hi!", Now, MessageStatus.Sent, isEvent: true);
        }, ct);
        return _store.GetContact(c.Username)!;
    }

    /// <summary>
    /// Declines a request and blocks the sender: nothing from them is shown again, including
    /// new requests, unless you add them yourself. They aren't told.
    /// </summary>
    public Task<Contact> DeclineRequestAsync(string username) => Locked(() =>
    {
        RequireRegistered();
        var c = _store.GetContact(username) ?? throw new MessengerException($"{username} is not a contact.");
        if (c.State == ContactState.Friend) throw new MessengerException($"{username} is already a friend.");
        using var tx = _store.Begin();
        SetState(username, ContactState.Blocked);
        tx.Commit();
        return _store.GetContact(username)!;
    });

    /// <summary>Tests only: records <paramref name="username"/> as a friend without the request round trip.</summary>
    internal Task MakeFriendForTestAsync(string username) => Locked(async () =>
    {
        var identity = await GetIdentityOrThrow(username, default);
        using var tx = _store.Begin();
        Trust(username, identity.IdentitySigPub, identity.IdentityDhPub);
        SetState(username, ContactState.Friend);
        tx.Commit();
        return true;
    });

    private void SetState(string username, ContactState state)
    {
        if (_store.GetContact(username) is { } c && c.State != state) _store.PutContact(c with { State = state });
    }

    /// <summary>Sends a request or acceptance. A hidden row carries its delivery, so it's retried like any message.</summary>
    private Task SendControlAsync(string peer, string type, Action apply, CancellationToken ct) =>
        FanOutAsync([peer], new MessageContent(type, "", 0), sentAt =>
        {
            apply();
            return _store.InsertMessage(peer, true, "", sentAt, MessageStatus.Pending, kind: "control");
        }, ct);

    /// <summary>Acceptances to send once the sync is done: for requests that crossed, and for friends who asked again (say they reinstalled).</summary>
    private readonly List<string> _acceptsToResend = new();

    /// <summary>
    /// Fetches the identity key the relay currently has for the user and the safety number
    /// it would give. Show the number to the user; if they confirm it matches what their
    /// contact sees, pass the result to <see cref="TrustIdentityAsync"/>.
    /// </summary>
    public Task<IdentityCheck> CheckIdentityAsync(string username, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        var identity = await GetIdentityOrThrow(username, ct);
        var known = _store.GetContact(username);
        var matches = known is not null && Same(known, identity.IdentitySigPub, identity.IdentityDhPub);
        return new IdentityCheck(username, identity.IdentitySigPub, identity.IdentityDhPub,
            ComputeSafetyNumber(username, identity.IdentitySigPub, identity.IdentityDhPub), matches);
    });

    /// <summary>
    /// Trusts exactly the key in <paramref name="check"/> (not whatever the relay has now),
    /// clears the key-change warning, and drops old sessions if the key differs. Messages
    /// held because of the change are processed on the next sync.
    /// </summary>
    public Task<Contact> TrustIdentityAsync(IdentityCheck check, bool verified = false) => Locked(() =>
    {
        RequireRegistered();
        var known = _store.GetContact(check.Username);
        var keyChanged = known is not null && !Same(known, check.IdentitySigPub, check.IdentityDhPub);
        var contact = new Contact(check.Username, check.IdentitySigPub, check.IdentityDhPub, known?.AddedAt ?? Now, verified, false,
            known?.State ?? ContactState.Stranger);
        using var tx = _store.Begin();
        _store.PutContact(contact);
        if (keyChanged) _store.DeleteSessions(check.Username);
        tx.Commit();
        return contact;
    });

    /// <summary>The safety number for the key you currently trust for this contact.</summary>
    public Task<string> SafetyNumberAsync(string username) => Locked(() =>
    {
        RequireRegistered();
        var c = _store.GetContact(username) ?? throw new MessengerException($"{username} is not a contact.");
        return ComputeSafetyNumber(username, c.IdentitySigPub, c.IdentityDhPub);
    });

    /// <summary>Records that the user compared safety numbers with this contact (or undoes it).</summary>
    public Task<Contact> SetVerifiedAsync(string username, bool verified) => Locked(() =>
    {
        var c = _store.GetContact(username) ?? throw new MessengerException($"{username} is not a contact.");
        if (c.IdentityChanged) throw new IdentityChangedException(username);
        using var tx = _store.Begin();
        var updated = c with { Verified = verified };
        _store.PutContact(updated);
        tx.Commit();
        return updated;
    });

    private string ComputeSafetyNumber(string peer, byte[] sigPub, byte[] dhPub) =>
        SafetyNumber.Compute(Username!, P.SigPubBytes(_identity!.SigPriv), P.DhPubBytes(_identity.DhPriv), peer, sigPub, dhPub);

    private static bool Same(Contact c, byte[] sigPub, byte[] dhPub) =>
        c.IdentitySigPub.AsSpan().SequenceEqual(sigPub) && c.IdentityDhPub.AsSpan().SequenceEqual(dhPub);

    /// <summary>Flags a contact whose key no longer matches. Returns false if already flagged.</summary>
    private bool FlagIdentityChanged(string username)
    {
        var c = _store.GetContact(username);
        if (c is null || c.IdentityChanged) return false;
        using var tx = _store.Begin();
        _store.PutContact(c with { IdentityChanged = true, Verified = false });
        tx.Commit();
        return true;
    }

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
            if (known.IdentityChanged || !Same(known, sigPub, dhPub)) throw new IdentityChangedException(username);
            return known;
        }
        var contact = new Contact(username, sigPub, dhPub, Now, State: ContactState.Stranger);
        _store.PutContact(contact);
        return contact;
    }

    // ================================================================ history

    public IReadOnlyList<StoredMessage> History(string peer, int limit = 50) => LockedSync<IReadOnlyList<StoredMessage>>(() => _store.History(peer, limit));

    public Task<IReadOnlyList<StoredMessage>> HistoryAsync(string peer, int limit = 50) =>
        Locked<IReadOnlyList<StoredMessage>>(() => _store.History(peer, limit));

    // ================================================================ sending

    /// <summary>
    /// Encrypts and queues a message, then tries to deliver it. If the relay is down the
    /// message stays Pending and the same ciphertext is retried on the next sync.
    /// </summary>
    public Task<StoredMessage> SendAsync(string peer, string text, CancellationToken ct = default) =>
        Locked(() => SendDirectAsync(peer, text, null, ct));

    /// <summary>
    /// Sends an image or video (with an optional caption). The file is encrypted with a fresh
    /// key and parked on the relay for the recipient; the key goes in the message.
    /// </summary>
    public Task<StoredMessage> SendFileAsync(string peer, string path, string caption = "", CancellationToken ct = default) => Locked(async () =>
    {
        CheckDirectPeer(peer);
        CheckText(caption);
        var media = await UploadAsync(path, [peer], ct);
        return await SendDirectAsync(peer, caption, media, ct);
    });

    private void CheckDirectPeer(string peer)
    {
        RequireRegistered();
        if (peer == Username) throw new MessengerException("You can't message yourself.");
        var c = _store.GetContact(peer);
        if (c is { IdentityChanged: true }) throw new IdentityChangedException(peer);
        RequireFriend(peer, c);
    }

    private static void RequireFriend(string peer, Contact? c)
    {
        switch (c?.State)
        {
            case ContactState.Friend: return;
            case ContactState.RequestSent: throw new NotFriendsException(peer, $"{peer} hasn't accepted your request yet.");
            case ContactState.RequestReceived: throw new NotFriendsException(peer, $"Accept {peer}'s request first.");
            default: throw new NotFriendsException(peer, $"You and {peer} aren't friends. Send a request first.");
        }
    }

    private async Task<StoredMessage> SendDirectAsync(string peer, string text, PreparedMedia? media, CancellationToken ct)
    {
        CheckDirectPeer(peer);
        CheckText(text);

        // A new session needs a bundle from the relay; fetch it before opening a transaction.
        var bundles = await FetchMissingBundlesAsync([peer], ct);

        long messageId;
        EnvelopeDto env;
        using (var tx = _store.Begin())
        {
            var sentAt = Now;
            var content = new MessageContent("text", text, sentAt.ToUnixTimeMilliseconds(), Attachment: media?.Pointer);
            env = EncryptOrFlag(tx, peer, bundles.GetValueOrDefault(peer), Serialize(content));
            messageId = _store.InsertMessage(peer, true, text, sentAt, MessageStatus.Pending);
            if (media is not null) KeepOwnCopy(messageId, media);
            _store.PutOutbox(messageId, peer, env);
            tx.Commit();
        }

        try { await DeliverAsync(messageId, peer, env, ct); }
        catch (RelayException e) when (e.Status is null) { /* stays pending; retried on sync */ }
        return _store.GetMessage(messageId);
    }

    private static byte[] Serialize(MessageContent content) => JsonSerializer.SerializeToUtf8Bytes(content, WireJson.Options);

    /// <summary>Takes a prekey bundle for each peer we have no session with yet.</summary>
    private async Task<Dictionary<string, BundleResponse>> FetchMissingBundlesAsync(IEnumerable<string> peers, CancellationToken ct)
    {
        var bundles = new Dictionary<string, BundleResponse>();
        foreach (var peer in peers)
        {
            if (_store.Sessions(peer).Count > 0) continue;
            try { bundles[peer] = await _relay.TakeBundleAsync(peer, ct); }
            catch (RelayException e) when (e.Status == System.Net.HttpStatusCode.NotFound) { throw new UserNotFoundException(peer); }
        }
        return bundles;
    }

    /// <summary>
    /// Encrypts on the session we send on (starting one from the bundle if needed) and saves
    /// the advanced state. Runs inside <paramref name="tx"/>; if the peer's key changed, rolls
    /// it back, flags the contact, and throws.
    /// </summary>
    private EnvelopeDto EncryptOrFlag(LocalStore.Tx tx, string peer, BundleResponse? bundle, byte[] plaintext)
    {
        (string Sid, SessionRecord Rec) session;
        try { session = bundle is null ? _store.Sessions(peer)[0] : StartSession(peer, bundle); }
        catch (IdentityChangedException)
        {
            tx.Dispose();
            FlagIdentityChanged(peer);
            throw;
        }
        var (sid, rec) = session;
        var state = RatchetState.FromDto(rec.State);
        var (header, ciphertext) = Ratchet.Encrypt(state, plaintext, rec.Ad);
        _store.PutSession(peer, sid, rec with { State = state.ToDto() }, Now);
        return new EnvelopeDto(rec.PendingInitial, header.Encode(), ciphertext);
    }

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

    // ================================================================ groups
    //
    // A group message is sent as a separate copy to each member over the ordinary pairwise
    // sessions ("client-side fan-out", as Signal did before Sender Keys). The relay sees only
    // one-to-one messages and never learns that a group exists. Every member keeps their own
    // copy of the member list. Updates can only add people; the only way out is to leave.
    // Messages that claim to be for a group are accepted only from its current members.

    public IReadOnlyList<Group> Groups() => LockedSync<IReadOnlyList<Group>>(_store.Groups);

    public Group? GetGroup(string groupId) => LockedSync(() => _store.GetGroup(groupId));

    public Task<IReadOnlyList<StoredMessage>> GroupHistoryAsync(string groupId, int limit = 50) =>
        Locked<IReadOnlyList<StoredMessage>>(() => _store.GroupHistory(groupId, limit));

    public IReadOnlyList<StoredMessage> GroupHistory(string groupId, int limit = 50) =>
        LockedSync<IReadOnlyList<StoredMessage>>(() => _store.GroupHistory(groupId, limit));

    /// <summary>Creates a group with you and <paramref name="members"/> and tells each of them.</summary>
    public Task<Group> CreateGroupAsync(string name, IEnumerable<string> members, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        name = name.Trim();
        if (name.Length is 0 or > MaxGroupNameLength) throw new MessengerException($"Group names are 1-{MaxGroupNameLength} characters.");
        var others = NormalizeMembers(members);
        if (others.Count == 0) throw new MessengerException("Add at least one other person to the group.");
        CheckGroupSize(others.Count);
        RequireFriends(others);

        var group = new Group(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            name, others, Now);
        await FanOutAsync(others, GroupUpdate(group), sentAt =>
        {
            _store.PutGroup(group);
            return _store.InsertMessage(Username!, true, $"You created the group with {string.Join(", ", others)}.", sentAt,
                MessageStatus.Pending, group.Id, isEvent: true);
        }, ct);
        return group;
    });

    /// <summary>Adds people to a group. Everyone, old members and new, gets the new member list.</summary>
    public Task<Group> AddGroupMembersAsync(string groupId, IEnumerable<string> members, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        var group = ActiveGroup(groupId);
        var added = NormalizeMembers(members).Except(group.Members).ToList();
        if (added.Count == 0) throw new MessengerException("They're already in the group.");
        CheckGroupSize(group.Members.Count + added.Count);
        RequireFriends(added);

        var updated = group with { Members = group.Members.Concat(added).Order().ToList() };
        await FanOutAsync(updated.Members, GroupUpdate(updated), sentAt =>
        {
            _store.PutGroup(updated);
            return _store.InsertMessage(Username!, true, $"You added {string.Join(", ", added)}.", sentAt, MessageStatus.Pending, groupId, isEvent: true);
        }, ct);
        return updated;
    });

    /// <summary>Tells the members you left and stops accepting the group's messages. History stays.</summary>
    public Task<Group> LeaveGroupAsync(string groupId, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        var group = ActiveGroup(groupId);
        var left = group with { Active = false };
        // Nothing secret goes out, so skip anyone whose key is in doubt rather than block leaving.
        var recipients = group.Members.Where(m => _store.GetContact(m) is not { IdentityChanged: true }).ToList();
        await FanOutAsync(recipients, new MessageContent("group_leave", "", 0, new GroupContext(groupId)), sentAt =>
        {
            _store.PutGroup(left);
            return _store.InsertMessage(Username!, true, "You left the group.", sentAt, MessageStatus.Pending, groupId, isEvent: true);
        }, ct, checkIdentities: false);
        return left;
    });

    /// <summary>Sends a message to everyone in the group. Blocked while any member's safety number change is unresolved.</summary>
    public Task<StoredMessage> SendGroupAsync(string groupId, string text, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        CheckText(text);
        var group = ActiveGroup(groupId);
        var id = await FanOutAsync(group.Members, new MessageContent("text", text, 0, new GroupContext(groupId)),
            sentAt => _store.InsertMessage(Username!, true, text, sentAt, MessageStatus.Pending, groupId), ct);
        return _store.GetMessage(id);
    });

    /// <summary>Sends an image or video to everyone in the group (see <see cref="SendFileAsync"/>).</summary>
    public Task<StoredMessage> SendGroupFileAsync(string groupId, string path, string caption = "", CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        CheckText(caption);
        var group = ActiveGroup(groupId);
        if (group.Members.Count == 0) throw new MessengerException("No one else is in this group.");
        if (group.Members.FirstOrDefault(m => _store.GetContact(m) is { IdentityChanged: true }) is { } flagged)
            throw new IdentityChangedException(flagged);
        var media = await UploadAsync(path, group.Members, ct);
        var id = await FanOutAsync(group.Members, new MessageContent("text", caption, 0, new GroupContext(groupId), media.Pointer), sentAt =>
        {
            var messageId = _store.InsertMessage(Username!, true, caption, sentAt, MessageStatus.Pending, groupId);
            KeepOwnCopy(messageId, media);
            return messageId;
        }, ct);
        return _store.GetMessage(id);
    });

    /// <summary>Tests only: the signed relay client, to act as this user directly.</summary>
    internal RelayClient RelayForTest => _relay;

    /// <summary>Tests only: sends arbitrary content to one peer, as a modified client could.</summary>
    internal Task SendRawForTestAsync(string peer, MessageContent content) => Locked(() =>
        FanOutAsync([peer], content, t => _store.InsertMessage(peer, true, "(raw)", t, MessageStatus.Pending), default));

    private MessageContent GroupUpdate(Group g) =>
        new("group_update", "", 0, new GroupContext(g.Id, g.Name, [.. g.Members, Username!]));

    private Group ActiveGroup(string groupId)
    {
        var group = _store.GetGroup(groupId) ?? throw new MessengerException("No such group.");
        if (!group.Active) throw new MessengerException("You left this group.");
        return group;
    }

    private List<string> NormalizeMembers(IEnumerable<string> members)
    {
        var list = members.Select(m => m.Trim().ToLowerInvariant()).Where(m => m.Length > 0 && m != Username).Distinct().Order().ToList();
        if (list.FirstOrDefault(m => !Usernames.IsValid(m)) is { } bad) throw new UserNotFoundException(bad);
        return list;
    }

    /// <summary>You can only put your own friends in a group (members can be strangers to each other).</summary>
    private void RequireFriends(IEnumerable<string> people)
    {
        var notFriends = people.Where(p => _store.GetContact(p) is not { State: ContactState.Friend }).ToList();
        if (notFriends.Count > 0)
            throw new NotFriendsException(notFriends[0], $"You can only add friends to a group. Not friends yet: {string.Join(", ", notFriends)}.");
    }

    private static void CheckGroupSize(int others)
    {
        if (others + 1 > MaxGroupSize) throw new MessengerException($"Groups can have at most {MaxGroupSize} people.");
    }

    /// <summary>
    /// Encrypts <paramref name="content"/> once per recipient, records the message (written by
    /// <paramref name="record"/>, in the same transaction) and the copies to deliver, then tries
    /// to deliver them. Copies the relay can't take now are retried on sync. Returns the message id.
    /// </summary>
    private async Task<long> FanOutAsync(IReadOnlyList<string> recipients, MessageContent content, Func<DateTimeOffset, long> record,
        CancellationToken ct, bool checkIdentities = true)
    {
        if (checkIdentities && recipients.FirstOrDefault(r => _store.GetContact(r) is { IdentityChanged: true }) is { } flagged)
            throw new IdentityChangedException(flagged);

        var bundles = await FetchMissingBundlesAsync(recipients, ct);
        long messageId;
        using (var tx = _store.Begin())
        {
            var sentAt = Now;
            var plaintext = Serialize(content with { SentAt = sentAt.ToUnixTimeMilliseconds() });
            var copies = recipients.Select(r => (Peer: r, Env: EncryptOrFlag(tx, r, bundles.GetValueOrDefault(r), plaintext))).ToList();
            messageId = record(sentAt);
            foreach (var (peer, env) in copies) _store.PutGroupOutbox(messageId, peer, env);
            if (copies.Count == 0) _store.SetMessageStatus(messageId, MessageStatus.Sent);
            tx.Commit();
        }
        await DeliverCopiesAsync(messageId, [], ct);
        return messageId;
    }

    /// <summary>Delivers a group message's remaining copies. Returns false if the relay is unreachable.</summary>
    private async Task<bool> DeliverCopiesAsync(long messageId, List<string> problems, CancellationToken ct)
    {
        foreach (var (peer, env) in _store.GroupOutbox(messageId))
        {
            var failed = false;
            try { await _relay.SendAsync(peer, env, ct); }
            catch (RelayException e) when (e.Status is null) { return false; }
            catch (RelayException e)
            {
                failed = true;
                problems.Add($"Could not deliver a group message to {peer}: {e.Message}");
            }
            using var tx = _store.Begin();
            _store.DeleteGroupOutbox(messageId, peer);
            if (failed) _store.SetMessageStatus(messageId, MessageStatus.Failed);
            else if (_store.GroupOutbox(messageId).Count == 0 && _store.GetMessage(messageId).Status == MessageStatus.Pending)
                _store.SetMessageStatus(messageId, MessageStatus.Sent);
            tx.Commit();
        }
        return true;
    }

    /// <summary>Applies a group message or update from <paramref name="sender"/>. Runs inside the caller's transaction.</summary>
    private (long? MessageId, string? Problem) ReceiveGroup(string sender, MessageContent content, DateTimeOffset sentAt, out bool isUpdate)
    {
        isUpdate = false;
        var ctx = content.Group!;
        if (ctx.Id.Length != 32 || !ctx.Id.All(Uri.IsHexDigit)) return (null, $"Ignored a malformed group message from {sender}.");
        var group = _store.GetGroup(ctx.Id);
        var isMember = group is { Active: true } && group.Members.Contains(sender);

        switch (content.Type)
        {
            case "text":
                if (group is { Active: false }) return (null, null); // we left; ignore quietly
                if (!isMember) return (null, $"Dropped a group message from {sender}: they aren't in that group.");
                return (_store.InsertMessage(sender, false, content.Body, sentAt, MessageStatus.Received, ctx.Id), null);

            case "reaction":
                if (!isMember) return (null, null);
                isUpdate = true;
                return (ApplyReaction(sender, ctx.Id, content.Reaction), null);

            case "group_leave":
                if (!isMember) return (null, null);
                _store.PutGroup(group! with { Members = group.Members.Where(m => m != sender).ToList() });
                return (_store.InsertMessage(sender, false, $"{sender} left.", sentAt, MessageStatus.Received, ctx.Id, isEvent: true), null);

            case "group_update":
                var members = ctx.Members ?? [];
                var name = ctx.Name?.Trim() ?? "";
                if (name.Length is 0 or > MaxGroupNameLength || members.Count > MaxGroupSize || !members.All(Usernames.IsValid)
                    || !members.Contains(sender) || !members.Contains(Username!))
                    return (null, $"Ignored a malformed group update from {sender}.");
                var others = members.Where(m => m != Username).Distinct().Order().ToList();

                if (group is not { Active: true })
                {
                    // Only a friend can put you in a group; invites from anyone else are ignored.
                    if (_store.GetContact(sender) is not { State: ContactState.Friend }) return (null, null);
                    // We were added (or added back after leaving).
                    _store.PutGroup(new Group(ctx.Id, name, others, group?.CreatedAt ?? Now));
                    return (_store.InsertMessage(sender, false, $"{sender} added you to “{name}”.", sentAt, MessageStatus.Received, ctx.Id, isEvent: true), null);
                }
                if (!isMember) return (null, $"Ignored a group update from {sender}: they aren't in that group.");
                var added = others.Except(group.Members).ToList();
                if (added.Count == 0) return (null, null);
                _store.PutGroup(group with { Members = group.Members.Concat(added).Order().ToList() });
                return (_store.InsertMessage(sender, false, $"{sender} added {string.Join(", ", added)}.", sentAt, MessageStatus.Received, ctx.Id, isEvent: true), null);

            default:
                return (null, null); // from a newer version; nothing we can show
        }
    }

    // ================================================================ attachments
    //
    // The sender encrypts each file with a fresh random AES-256-GCM key and uploads only the
    // ciphertext to the relay, naming who may fetch it. The key, the ciphertext's SHA-256, and
    // the relay's id travel inside the ratchet message. Each recipient downloads, checks the
    // hash, decrypts, saves the file on their own device (encrypted under the profile's master
    // key), and acknowledges; the relay deletes the blob once everyone has it.

    private static readonly byte[] AttachmentAd = Encoding.UTF8.GetBytes("COSC483-ATTACHMENT-v1");

    private sealed record PreparedMedia(AttachmentPointer Pointer, byte[] Plaintext);

    private async Task<PreparedMedia> UploadAsync(string path, IReadOnlyCollection<string> recipients, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new MessengerException($"File not found: {path}");
        var type = MediaTypes.ForFileName(info.Name)
            ?? throw new MessengerException($"That kind of file can't be sent. You can send {MediaTypes.Accepted}.");
        if (info.Length == 0) throw new MessengerException("That file is empty.");
        if (info.Length > Attachments.MaxPlaintextBytes)
            throw new MessengerException($"That file is {MediaTypes.FormatSize(info.Length)}; the limit is {MediaTypes.FormatSize(Attachments.MaxPlaintextBytes)}.");

        var plaintext = await File.ReadAllBytesAsync(path, ct);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var ciphertext = P.AeadEncrypt(key, plaintext, AttachmentAd);
        var id = await _relay.UploadAttachmentAsync(ciphertext, recipients, ct);
        return new PreparedMedia(new AttachmentPointer(id, key, System.Security.Cryptography.SHA256.HashData(ciphertext),
            plaintext.Length, type, MediaTypes.CleanFileName(info.Name)), plaintext);
    }

    /// <summary>The sender keeps the file too, so it shows in their own history. Runs inside a transaction.</summary>
    private void KeepOwnCopy(long messageId, PreparedMedia media) =>
        _store.PutAttachment(messageId, media.Pointer, AttachmentState.Ready, _store.WriteMedia(media.Plaintext));

    /// <summary>Checks a received pointer and queues the download. Runs inside the caller's transaction.</summary>
    private string? AcceptAttachment(long messageId, string sender, AttachmentPointer p)
    {
        var name = MediaTypes.CleanFileName(p.FileName ?? "");
        // The type comes from our own list, never from what the sender claimed.
        if (MediaTypes.ForFileName(name) is not { } type || !Attachments.IsValidId(p.Id) || p.Key?.Length != 32
            || p.Digest?.Length != 32 || p.Size is <= 0 or > Attachments.MaxPlaintextBytes)
            return $"{sender} sent a kind of file this app doesn't accept; it was ignored.";
        _store.PutAttachment(messageId, p with { FileName = name, ContentType = type }, AttachmentState.Downloading, null);
        return null;
    }

    /// <summary>Fetches one queued attachment. Returns null if the relay is unreachable (try again later).</summary>
    private async Task<StoredMessage?> DownloadAsync(long messageId, List<string> problems, CancellationToken ct)
    {
        var (p, _, _) = _store.GetAttachment(messageId)!.Value;
        var sender = _store.GetMessage(messageId).Peer;
        string? failure = null;
        byte[]? plaintext = null;
        try
        {
            var blob = await _relay.DownloadAttachmentAsync(p.Id, ct);
            if (!System.Security.Cryptography.SHA256.HashData(blob).AsSpan().SequenceEqual(p.Digest))
                failure = $"An attachment from {sender} didn't match what they sent (the relay changed it), so it was discarded.";
            else
            {
                plaintext = P.AeadDecrypt(p.Key, blob, AttachmentAd);
                if (plaintext.Length != p.Size) failure = $"An attachment from {sender} had the wrong size, so it was discarded.";
            }
        }
        catch (RelayException e) when (e.Status is null) { return null; }
        catch (RelayException e) { failure = $"Couldn't download an attachment from {sender}: {e.Message}"; }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            failure = $"An attachment from {sender} couldn't be decrypted, so it was discarded.";
        }

        using (var tx = _store.Begin())
        {
            if (failure is null) _store.SetAttachmentState(messageId, AttachmentState.Ready, _store.WriteMedia(plaintext!));
            else _store.SetAttachmentState(messageId, AttachmentState.Failed, null);
            tx.Commit();
        }
        if (failure is null)
        {
            try { await _relay.AckAttachmentAsync(p.Id, ct); }
            catch (RelayException) { /* the relay deletes it after the retention period anyway */ }
        }
        else problems.Add(failure);
        return _store.GetMessage(messageId);
    }

    /// <summary>The decrypted file, for showing an image.</summary>
    public Task<byte[]> AttachmentBytesAsync(long messageId) => Locked(() => _store.ReadMedia(ReadyFile(messageId).File));

    /// <summary>
    /// Writes the attachment out as an ordinary file in <paramref name="directory"/> (to keep it,
    /// or to open it in another app) and returns its path. The extension comes from the
    /// attachment's type, so a file can't pose as a program.
    /// </summary>
    public Task<string> ExportAttachmentAsync(long messageId, string directory) => Locked(() =>
    {
        var (p, file) = ReadyFile(messageId);
        Directory.CreateDirectory(directory);
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '/', '\\']).ToHashSet();
        var stem = new string(Path.GetFileNameWithoutExtension(p.FileName).Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimStart('.');
        if (stem.Length == 0) stem = "attachment";
        var ext = MediaTypes.ExtensionFor(p.ContentType);
        var path = Path.Combine(directory, stem + ext);
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(directory, $"{stem} ({i}){ext}");
        File.WriteAllBytes(path, _store.ReadMedia(file));
        return path;
    });

    /// <summary>Where plaintext copies made for opening in other apps go. Emptied when the profile closes.</summary>
    public string OpenFolder => Path.Combine(_dataDir, "open");

    private (AttachmentPointer Pointer, string File) ReadyFile(long messageId)
    {
        var a = _store.GetAttachment(messageId) ?? throw new MessengerException("That message has no attachment.");
        if (a.State != AttachmentState.Ready || a.File is null) throw new MessengerException("That attachment hasn't been downloaded.");
        return (a.Pointer, a.File);
    }

    // ================================================================ reactions

    /// <summary>The quick reactions the app offers. Any short emoji is accepted from others.</summary>
    public static readonly IReadOnlyList<string> QuickReactions = ["👍", "❤️", "😂", "😮", "😢", "🙏"];

    private static bool IsValidReaction(string? emoji) =>
        emoji is { Length: > 0 and <= 16 } && !emoji.Any(char.IsControl) && !string.IsNullOrWhiteSpace(emoji) && !emoji.Any(char.IsAsciiLetterOrDigit);

    /// <summary>Reacts to a message, replacing your earlier reaction; null takes it back. Everyone in the chat is told.</summary>
    public Task<StoredMessage> ReactAsync(long messageId, string? emoji, CancellationToken ct = default) => Locked(async () =>
    {
        RequireRegistered();
        if (emoji is not null && !IsValidReaction(emoji)) throw new MessengerException("That isn't a reaction.");
        var target = _store.GetMessage(messageId);
        if (target.IsEvent) throw new MessengerException("You can only react to messages.");
        var author = target.Outgoing ? Username! : target.Peer;
        var reaction = new ReactionContent(author, target.SentAt.ToUnixTimeMilliseconds(), emoji);
        IReadOnlyList<string> recipients;
        if (target.GroupId is { } gid) recipients = ActiveGroup(gid).Members;
        else
        {
            CheckDirectPeer(target.Peer);
            recipients = [target.Peer];
        }
        var content = new MessageContent("reaction", "", 0, target.GroupId is null ? null : new GroupContext(target.GroupId), Reaction: reaction);
        await FanOutAsync(recipients, content, sentAt =>
        {
            _store.SetReaction(messageId, Username!, emoji);
            // Never shown; it carries the delivery of the reaction's copies.
            return _store.InsertMessage(target.GroupId is null ? target.Peer : Username!, true, "", sentAt, MessageStatus.Pending, target.GroupId, kind: "control");
        }, ct);
        return _store.GetMessage(messageId);
    });

    /// <summary>Applies a reaction from <paramref name="sender"/>. Returns the message it changed, if any. Runs inside a transaction.</summary>
    private long? ApplyReaction(string sender, string? groupId, ReactionContent? r)
    {
        if (r is null || !Usernames.IsValid(r.Author) || (r.Emoji is not null && !IsValidReaction(r.Emoji))) return null;
        // In a direct chat, only messages between the two of you can be reacted to.
        if (groupId is null && r.Author != sender && r.Author != Username) return null;
        if (_store.FindMessage(groupId, sender, r.Author, r.Author == Username, r.SentAt) is not { } target) return null;
        _store.SetReaction(target, sender, r.Emoji);
        return target;
    }

    // ================================================================ receiving

    /// <summary>
    /// Waits until the relay has mail for us or <paramref name="timeout"/> passes, without
    /// blocking other operations. Returns true if there is mail; call <see cref="SyncAsync"/>
    /// to fetch it. The relay's answer carries no message content.
    /// </summary>
    public async Task<bool> WaitForMessagesAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        RequireRegistered();
        return await _relay.WaitAsync((int)Math.Clamp(timeout.TotalSeconds, 0, RelayClient.MaxWaitSeconds), Interlocked.Read(ref _lastSeenInboxId), ct)
            .ConfigureAwait(false);
    }

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

        var reachable = true;
        foreach (var (messageId, peer, env) in _store.Outbox())
        {
            try { await DeliverAsync(messageId, peer, env, ct); }
            catch (RelayException e) when (e.Status is null) { reachable = false; break; }
            catch (RelayException e) { problems.Add($"Could not deliver a message to {peer}: {e.Message}"); }
        }
        if (reachable)
            foreach (var messageId in _store.GroupOutboxMessages())
                if (!await DeliverCopiesAsync(messageId, problems, ct)) break;

        var received = new List<StoredMessage>();
        var updated = new List<StoredMessage>();
        var changed = new List<string>();
        var consumedPrekey = false;
        while (true)
        {
            var inbox = await _relay.FetchAsync(ct);
            if (inbox.Messages.Count == 0) break;
            Interlocked.Exchange(ref _lastSeenInboxId, Math.Max(Interlocked.Read(ref _lastSeenInboxId), inbox.Messages.Max(m => m.Id)));
            var handled = new List<long>();
            foreach (var item in inbox.Messages)
            {
                if (!_store.IsProcessed(item.Id))
                {
                    var outcome = await ProcessAsync(item, ct);
                    if (outcome.Message is not null) received.Add(outcome.Message);
                    if (outcome.Updated is not null) updated.Add(outcome.Updated);
                    if (outcome.Problem is not null) problems.Add(outcome.Problem);
                    consumedPrekey |= outcome.UsedPrekey;
                    if (outcome.Held)
                    {
                        if (outcome.Problem is not null) changed.Add(item.Sender);
                        continue; // stays on the relay until the user decides
                    }
                }
                handled.Add(item.Id);
            }
            if (handled.Count > 0) await _relay.AckAsync(handled, ct);
            if (inbox.Messages.Count < 100 || handled.Count == 0) break;
        }

        // Attachments that arrived (now or earlier, if the relay was down) are fetched last.
        foreach (var messageId in _store.AttachmentsToDownload())
        {
            if (await DownloadAsync(messageId, problems, ct) is not { } done) break;
            if (received.FindIndex(m => m.Id == done.Id) is var i and >= 0) received[i] = done;
            else updated.Add(done);
        }

        List<string> resend;
        lock (_acceptsToResend) { resend = _acceptsToResend.Distinct().ToList(); _acceptsToResend.Clear(); }
        foreach (var peer in resend)
        {
            try { await SendControlAsync(peer, "contact_accept", () => { }, ct); }
            catch (Exception e) when (e is RelayException or MessengerException) { problems.Add($"Could not answer {peer}'s request: {e.Message}"); }
        }

        await MaintainPrekeysAsync(consumedPrekey, ct);
        // Report each message once, as it is now: a message that arrived and was reacted to in
        // the same sync is just "received", with its reactions.
        for (var i = 0; i < received.Count; i++) received[i] = _store.GetMessage(received[i].Id);
        var receivedIds = received.Select(m => m.Id).ToHashSet();
        var latest = updated.Select(m => m.Id).Distinct().Where(id => !receivedIds.Contains(id)).Select(_store.GetMessage).ToList();
        return new SyncResult(received, problems, changed, latest);
    });

    private sealed record Outcome(StoredMessage? Message, string? Problem, bool UsedPrekey, bool Held = false, StoredMessage? Updated = null);

    private async Task<Outcome> ProcessAsync(InboxItem item, CancellationToken ct)
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
            // The session advanced either way, so the transaction commits even if the content is refused.
            var isUpdate = false;
            var friend = _store.GetContact(sender) is { State: ContactState.Friend };
            var (id, problem) = content switch
            {
                { Group: not null } => ReceiveGroup(sender, content, sentAt, out isUpdate),
                { Type: "contact_request" or "contact_accept" } => (ReceiveContactControl(sender, content.Type, sentAt), (string?)null),
                // Direct messages only from friends. Anyone else's are dropped unseen.
                _ when !friend => ((long?)null, (string?)null),
                { Type: "text" } => ((long?)_store.InsertMessage(sender, false, content.Body, sentAt, MessageStatus.Received), (string?)null),
                { Type: "reaction" } => (ApplyReaction(sender, null, content.Reaction), (string?)null),
                _ => ((long?)null, (string?)null),
            };
            if (content.Type == "reaction") isUpdate = true;
            else if (id is { } newId && content is { Type: "text", Attachment: { } pointer })
                problem ??= AcceptAttachment(newId, sender, pointer);
            _store.MarkProcessed(item.Id, problem is null ? "ok" : "refused");
            tx.Commit();
            var stored = id is { } sid ? _store.GetMessage(sid) : null;
            return isUpdate ? new Outcome(null, problem, usedPrekey, Updated: stored) : new Outcome(stored, problem, usedPrekey);
        }
        catch (IdentityChangedException)
        {
            // A new session from a contact under a different key: keep the message on the
            // relay, unread, until the user checks the safety number and trusts (or not).
            tx.Dispose();
            var newlyFlagged = FlagIdentityChanged(sender);
            return new Outcome(null, newlyFlagged ? $"{sender}'s safety number changed. Their new messages are on hold until you verify it." : null, false, Held: true);
        }
        catch (Exception e) when (e is ProtocolException or JsonException
                                      or System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
        {
            tx.Dispose();
            return Drop(item, $"Dropped a message from {sender}: it could not be decrypted ({e.Message})");
        }
    }

    /// <summary>A contact request or acceptance from <paramref name="sender"/>. Runs inside the caller's transaction.</summary>
    private long? ReceiveContactControl(string sender, string type, DateTimeOffset sentAt)
    {
        var c = _store.GetContact(sender);
        if (c is null) return null; // Decrypt always records the sender first
        switch (type, c.State)
        {
            case ("contact_request", ContactState.Stranger):
                SetState(sender, ContactState.RequestReceived);
                return _store.InsertMessage(sender, false, $"{sender} wants to chat with you.", sentAt, MessageStatus.Received, isEvent: true);
            case ("contact_request", ContactState.RequestSent):
                // You both asked: that's a yes from each side. Confirm it, in case they saw
                // (and set aside) your request before sending theirs.
                SetState(sender, ContactState.Friend);
                lock (_acceptsToResend) _acceptsToResend.Add(sender);
                return _store.InsertMessage(sender, false, $"You and {sender} are now friends.", sentAt, MessageStatus.Received, isEvent: true);
            case ("contact_request", ContactState.Friend):
                // They lost track of it (say they reinstalled); answer again once this sync is done.
                lock (_acceptsToResend) _acceptsToResend.Add(sender);
                return null;
            case ("contact_accept", ContactState.RequestSent):
                SetState(sender, ContactState.Friend);
                return _store.InsertMessage(sender, false, $"{sender} accepted your request.", sentAt, MessageStatus.Received, isEvent: true);
            default:
                return null; // blocked, already decided, or an acceptance nobody asked for
        }
    }

    private Outcome Drop(InboxItem item, string problem)
    {
        using var tx = _store.Begin();
        _store.MarkProcessed(item.Id, "dropped");
        tx.Commit();
        return new Outcome(null, problem, false);
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
                throw new ProtocolException("the identity key in it is not the one registered for the sender");
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
