// The profile's local SQLite database. Private keys, ratchet state, message bodies, and
// queued envelopes are encrypted with the master key; usernames, contact public keys,
// and timestamps are not (they are metadata the relay sees anyway).

using System.Text;
using System.Text.Json;
using E2ee.Wire;
using Microsoft.Data.Sqlite;

namespace E2ee.Messaging;

public enum MessageStatus { Pending, Sent, Received, Failed }

/// <param name="Peer">The other person in a direct chat. In a group chat, who sent it (you, for outgoing).</param>
/// <param name="GroupId">Set for messages in a group chat.</param>
/// <param name="IsEvent">A line about the group itself ("bob left") rather than something someone wrote.</param>
/// <param name="Attachment">An image or video, if the message carries one (Text is then its caption).</param>
/// <param name="Reactions">Who reacted with what. Empty when nobody has.</param>
public sealed record StoredMessage(long Id, string Peer, bool Outgoing, string Text, DateTimeOffset SentAt, MessageStatus Status,
    string? GroupId = null, bool IsEvent = false, Attachment? Attachment = null, IReadOnlyList<Reaction>? Reactions = null)
{
    public IReadOnlyList<Reaction> Reactions { get; init; } = Reactions ?? [];
}

/// <summary>A group chat as this device knows it. Members are everyone else in it, not you.</summary>
/// <param name="Active">False after you left; the history stays.</param>
public sealed record Group(string Id, string Name, IReadOnlyList<string> Members, DateTimeOffset CreatedAt, bool Active = true);

/// <param name="Verified">The user compared safety numbers with this contact and confirmed them.</param>
/// <param name="IdentityChanged">Someone presented a different identity key for this contact. Their
/// messages are held and sending is blocked until the user checks and trusts a key.</param>
public sealed record Contact(string Username, byte[] IdentitySigPub, byte[] IdentityDhPub, DateTimeOffset AddedAt,
    bool Verified = false, bool IdentityChanged = false);

/// <summary>One Double Ratchet session with a peer. PendingInitial is resent with every
/// outgoing message until the peer's first reply arrives on this session.</summary>
internal sealed record SessionRecord(RatchetStateDto State, byte[] Ad, InitialDto? PendingInitial);

/// <summary>AES-256-GCM under the master key. The AD names the row, so a ciphertext
/// moved to another row or column fails to decrypt.</summary>
internal sealed class StateCipher(byte[] masterKey)
{
    public byte[] Seal(byte[] plaintext, string context) => Primitives.AeadEncrypt(masterKey, plaintext, Encoding.UTF8.GetBytes(context));
    public byte[] Open(byte[] ciphertext, string context) => Primitives.AeadDecrypt(masterKey, ciphertext, Encoding.UTF8.GetBytes(context));
}

internal sealed class LocalStore : IDisposable
{
    private readonly SqliteConnection _c;
    private readonly StateCipher _cipher;
    private SqliteTransaction? _tx;

    private readonly string _mediaDir;

    public LocalStore(string dbPath, byte[] masterKey)
    {
        _cipher = new StateCipher(masterKey);
        _mediaDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "media");
        Directory.CreateDirectory(_mediaDir);
        _c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        _c.Open();
        Exec("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS secrets (key TEXT PRIMARY KEY, value BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS prekeys (
                kind TEXT NOT NULL, id INTEGER NOT NULL, priv BLOB NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY (kind, id));
            CREATE TABLE IF NOT EXISTS contacts (
                username TEXT PRIMARY KEY, sig_pub BLOB NOT NULL, dh_pub BLOB NOT NULL, added_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions (
                peer TEXT NOT NULL, session_id TEXT NOT NULL, state BLOB NOT NULL,
                created_at INTEGER NOT NULL, last_used INTEGER NOT NULL, PRIMARY KEY (peer, session_id));
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT, peer TEXT NOT NULL, outgoing INTEGER NOT NULL,
                body BLOB NOT NULL, sent_at INTEGER NOT NULL, status TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS messages_peer ON messages (peer, id);
            CREATE TABLE IF NOT EXISTS outbox (message_id INTEGER PRIMARY KEY, peer TEXT NOT NULL, envelope BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS processed (server_id INTEGER PRIMARY KEY, outcome TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS groups (
                id TEXT PRIMARY KEY, name BLOB NOT NULL, created_at INTEGER NOT NULL, active INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS group_members (group_id TEXT NOT NULL, username TEXT NOT NULL, PRIMARY KEY (group_id, username));
            CREATE TABLE IF NOT EXISTS group_outbox (
                message_id INTEGER NOT NULL, peer TEXT NOT NULL, envelope BLOB NOT NULL, PRIMARY KEY (message_id, peer));
            CREATE TABLE IF NOT EXISTS attachments (
                message_id INTEGER PRIMARY KEY, pointer BLOB NOT NULL, state TEXT NOT NULL, file TEXT);
            CREATE TABLE IF NOT EXISTS reactions (
                message_id INTEGER NOT NULL, reactor TEXT NOT NULL, emoji TEXT NOT NULL, PRIMARY KEY (message_id, reactor));
            """);
        Migrate();
    }

    /// <summary>Adds columns introduced after a profile was created.</summary>
    private void Migrate()
    {
        var columns = new HashSet<string>();
        using (var cmd = Cmd("SELECT name FROM pragma_table_info('contacts')"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(0));
        if (!columns.Contains("verified")) Exec("ALTER TABLE contacts ADD COLUMN verified INTEGER NOT NULL DEFAULT 0");
        if (!columns.Contains("identity_changed")) Exec("ALTER TABLE contacts ADD COLUMN identity_changed INTEGER NOT NULL DEFAULT 0");

        columns.Clear();
        using (var cmd = Cmd("SELECT name FROM pragma_table_info('messages')"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) columns.Add(r.GetString(0));
        if (!columns.Contains("group_id")) Exec("ALTER TABLE messages ADD COLUMN group_id TEXT");
        if (!columns.Contains("kind")) Exec("ALTER TABLE messages ADD COLUMN kind TEXT NOT NULL DEFAULT 'text'");
        Exec("CREATE INDEX IF NOT EXISTS messages_group ON messages (group_id, id)");
    }

    public void Dispose()
    {
        _tx?.Dispose();
        _c.Dispose();
    }

    // ------------------------------------------------------------ transactions
    public Tx Begin()
    {
        if (_tx is not null) throw new InvalidOperationException("nested transaction");
        _tx = _c.BeginTransaction();
        return new Tx(this);
    }

    /// <summary>Rolls back unless committed. Disposing twice is harmless.</summary>
    public sealed class Tx(LocalStore s) : IDisposable
    {
        private readonly SqliteTransaction _inner = s._tx!;
        private bool _committed, _disposed;

        public void Commit()
        {
            _inner.Commit();
            _committed = true;
            Dispose(); // release the connection for reads right away
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_committed) _inner.Rollback();
            _inner.Dispose();
            if (ReferenceEquals(s._tx, _inner)) s._tx = null;
        }
    }

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Cmd(sql, args);
        cmd.ExecuteNonQuery();
    }

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();
    private static DateTimeOffset FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    // ------------------------------------------------------------ meta and secrets
    public string? GetMeta(string key)
    {
        using var cmd = Cmd("SELECT value FROM meta WHERE key = $k", ("$k", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value) =>
        Exec("INSERT INTO meta VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    public byte[]? GetSecret(string key)
    {
        using var cmd = Cmd("SELECT value FROM secrets WHERE key = $k", ("$k", key));
        return cmd.ExecuteScalar() is byte[] sealedValue ? _cipher.Open(sealedValue, $"secret|{key}") : null;
    }

    public void SetSecret(string key, byte[] value) =>
        Exec("INSERT INTO secrets VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$k", key), ("$v", _cipher.Seal(value, $"secret|{key}")));

    // ------------------------------------------------------------ prekeys
    public void PutPrekey(string kind, uint id, byte[] priv, DateTimeOffset createdAt) =>
        Exec("INSERT OR REPLACE INTO prekeys VALUES ($k, $i, $p, $t)",
            ("$k", kind), ("$i", (long)id), ("$p", _cipher.Seal(priv, $"prekey|{kind}|{id}")), ("$t", Ms(createdAt)));

    public byte[]? GetPrekey(string kind, uint id)
    {
        using var cmd = Cmd("SELECT priv FROM prekeys WHERE kind = $k AND id = $i", ("$k", kind), ("$i", (long)id));
        return cmd.ExecuteScalar() is byte[] v ? _cipher.Open(v, $"prekey|{kind}|{id}") : null;
    }

    public List<(uint Id, DateTimeOffset CreatedAt)> ListPrekeys(string kind)
    {
        using var cmd = Cmd("SELECT id, created_at FROM prekeys WHERE kind = $k ORDER BY id", ("$k", kind));
        using var r = cmd.ExecuteReader();
        var list = new List<(uint, DateTimeOffset)>();
        while (r.Read()) list.Add(((uint)r.GetInt64(0), FromMs(r.GetInt64(1))));
        return list;
    }

    public void DeletePrekey(string kind, uint id) =>
        Exec("DELETE FROM prekeys WHERE kind = $k AND id = $i", ("$k", kind), ("$i", (long)id));

    // ------------------------------------------------------------ contacts
    private const string ContactColumns = "username, sig_pub, dh_pub, added_at, verified, identity_changed";

    private static Contact ReadContact(SqliteDataReader r) =>
        new(r.GetString(0), (byte[])r[1], (byte[])r[2], FromMs(r.GetInt64(3)), r.GetInt64(4) == 1, r.GetInt64(5) == 1);

    public Contact? GetContact(string username)
    {
        using var cmd = Cmd($"SELECT {ContactColumns} FROM contacts WHERE username = $u", ("$u", username));
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadContact(r) : null;
    }

    public void PutContact(Contact c) =>
        Exec($"INSERT OR REPLACE INTO contacts ({ContactColumns}) VALUES ($u, $s, $d, $t, $v, $c)",
            ("$u", c.Username), ("$s", c.IdentitySigPub), ("$d", c.IdentityDhPub), ("$t", Ms(c.AddedAt)),
            ("$v", c.Verified ? 1 : 0), ("$c", c.IdentityChanged ? 1 : 0));

    public List<Contact> Contacts()
    {
        using var cmd = Cmd($"SELECT {ContactColumns} FROM contacts ORDER BY username");
        using var r = cmd.ExecuteReader();
        var list = new List<Contact>();
        while (r.Read()) list.Add(ReadContact(r));
        return list;
    }

    // ------------------------------------------------------------ sessions
    private byte[] SealSession(string peer, string sid, SessionRecord rec) =>
        _cipher.Seal(JsonSerializer.SerializeToUtf8Bytes(rec, WireJson.Options), $"session|{peer}|{sid}");

    private SessionRecord OpenSession(string peer, string sid, byte[] blob) =>
        JsonSerializer.Deserialize<SessionRecord>(_cipher.Open(blob, $"session|{peer}|{sid}"), WireJson.Options)!;

    /// <summary>Most recently used first; the first one is the session we send on.</summary>
    public List<(string Sid, SessionRecord Record)> Sessions(string peer)
    {
        using var cmd = Cmd("SELECT session_id, state FROM sessions WHERE peer = $p ORDER BY last_used DESC, created_at DESC", ("$p", peer));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, SessionRecord)>();
        while (r.Read()) list.Add((r.GetString(0), OpenSession(peer, r.GetString(0), (byte[])r[1])));
        return list;
    }

    public SessionRecord? GetSession(string peer, string sid)
    {
        using var cmd = Cmd("SELECT state FROM sessions WHERE peer = $p AND session_id = $s", ("$p", peer), ("$s", sid));
        return cmd.ExecuteScalar() is byte[] blob ? OpenSession(peer, sid, blob) : null;
    }

    public void PutSession(string peer, string sid, SessionRecord rec, DateTimeOffset now) =>
        Exec("""
            INSERT INTO sessions VALUES ($p, $s, $st, $t, $t)
            ON CONFLICT (peer, session_id) DO UPDATE SET state = excluded.state, last_used = excluded.last_used
            """, ("$p", peer), ("$s", sid), ("$st", SealSession(peer, sid, rec)), ("$t", Ms(now)));

    public void PruneSessions(string peer, int keep) =>
        Exec("""
            DELETE FROM sessions WHERE peer = $p AND session_id NOT IN (
                SELECT session_id FROM sessions WHERE peer = $p ORDER BY last_used DESC, created_at DESC LIMIT $n)
            """, ("$p", peer), ("$n", keep));

    public void DeleteSessions(string peer) => Exec("DELETE FROM sessions WHERE peer = $p", ("$p", peer));

    // ------------------------------------------------------------ messages
    /// <param name="kind">"text", "event" (a line about a group) or "control" (never shown; carries a reaction's delivery).</param>
    public long InsertMessage(string peer, bool outgoing, string text, DateTimeOffset sentAt, MessageStatus status,
        string? groupId = null, bool isEvent = false, string? kind = null)
    {
        using var cmd = Cmd("""
            INSERT INTO messages (peer, outgoing, body, sent_at, status, group_id, kind)
            VALUES ($p, $o, x'', $t, $s, $g, $k) RETURNING id
            """, ("$p", peer), ("$o", outgoing ? 1 : 0), ("$t", Ms(sentAt)), ("$s", status.ToString()), ("$g", groupId),
            ("$k", kind ?? (isEvent ? "event" : "text")));
        var id = (long)cmd.ExecuteScalar()!;
        Exec("UPDATE messages SET body = $b WHERE id = $i", ("$b", _cipher.Seal(Encoding.UTF8.GetBytes(text), $"message|{id}")), ("$i", id));
        return id;
    }

    public void SetMessageStatus(long id, MessageStatus status) =>
        Exec("UPDATE messages SET status = $s WHERE id = $i", ("$s", status.ToString()), ("$i", id));

    private StoredMessage ReadMessage(SqliteDataReader r)
    {
        var id = r.GetInt64(0);
        var text = Encoding.UTF8.GetString(_cipher.Open((byte[])r[3], $"message|{id}"));
        return new StoredMessage(id, r.GetString(1), r.GetInt64(2) == 1, text, FromMs(r.GetInt64(4)), Enum.Parse<MessageStatus>(r.GetString(5)),
            r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7) == "event");
    }

    private const string MessageColumns = "id, peer, outgoing, body, sent_at, status, group_id, kind";

    public StoredMessage GetMessage(long id)
    {
        StoredMessage m;
        using (var cmd = Cmd($"SELECT {MessageColumns} FROM messages WHERE id = $i", ("$i", id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) throw new MessengerException("No such message.");
            m = ReadMessage(r);
        }
        return WithExtras(m);
    }

    /// <summary>Adds the attachment and reactions, which live in their own tables.</summary>
    private StoredMessage WithExtras(StoredMessage m)
    {
        Attachment? attachment = null;
        if (GetAttachment(m.Id) is { } a)
            attachment = new Attachment(a.Pointer.FileName, a.Pointer.ContentType, a.Pointer.Size, a.State);
        var reactions = new List<Reaction>();
        using (var cmd = Cmd("SELECT reactor, emoji FROM reactions WHERE message_id = $i ORDER BY rowid", ("$i", m.Id)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) reactions.Add(new Reaction(r.GetString(0), r.GetString(1)));
        return m with { Attachment = attachment, Reactions = reactions };
    }

    /// <summary>
    /// The message a reaction points at: written by <paramref name="author"/> at <paramref name="sentAtMs"/>,
    /// in the group, or else in the direct chat with <paramref name="chatPeer"/>.
    /// </summary>
    public long? FindMessage(string? groupId, string chatPeer, string author, bool authorIsMe, long sentAtMs)
    {
        using var cmd = groupId is not null
            ? Cmd("SELECT id FROM messages WHERE group_id = $g AND peer = $a AND sent_at = $t AND kind = 'text' LIMIT 1",
                ("$g", groupId), ("$a", author), ("$t", sentAtMs))
            : Cmd("SELECT id FROM messages WHERE group_id IS NULL AND peer = $p AND outgoing = $o AND sent_at = $t AND kind = 'text' LIMIT 1",
                ("$p", chatPeer), ("$o", authorIsMe ? 1 : 0), ("$t", sentAtMs));
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>The newest <paramref name="limit"/> messages in the direct chat with the peer, oldest first.</summary>
    public List<StoredMessage> History(string peer, int limit) => ReadMessages("peer = $p AND group_id IS NULL", peer, limit);

    /// <summary>The newest <paramref name="limit"/> messages in the group, oldest first.</summary>
    public List<StoredMessage> GroupHistory(string groupId, int limit) => ReadMessages("group_id = $p", groupId, limit);

    private List<StoredMessage> ReadMessages(string where, string key, int limit)
    {
        var list = new List<StoredMessage>();
        using (var cmd = Cmd($"""
                   SELECT * FROM (SELECT {MessageColumns} FROM messages WHERE {where} AND kind != 'control' ORDER BY id DESC LIMIT $n)
                   ORDER BY id
                   """, ("$p", key), ("$n", limit)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) list.Add(ReadMessage(r));
        return list.Select(WithExtras).ToList();
    }

    // ------------------------------------------------------------ attachments and reactions
    public void PutAttachment(long messageId, AttachmentPointer pointer, AttachmentState state, string? file) =>
        Exec("INSERT OR REPLACE INTO attachments VALUES ($i, $p, $s, $f)", ("$i", messageId),
            ("$p", _cipher.Seal(JsonSerializer.SerializeToUtf8Bytes(pointer, WireJson.Options), $"attachment|{messageId}")),
            ("$s", state.ToString()), ("$f", file));

    public void SetAttachmentState(long messageId, AttachmentState state, string? file) =>
        Exec("UPDATE attachments SET state = $s, file = $f WHERE message_id = $i", ("$s", state.ToString()), ("$f", file), ("$i", messageId));

    public (AttachmentPointer Pointer, AttachmentState State, string? File)? GetAttachment(long messageId)
    {
        using var cmd = Cmd("SELECT pointer, state, file FROM attachments WHERE message_id = $i", ("$i", messageId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var pointer = JsonSerializer.Deserialize<AttachmentPointer>(_cipher.Open((byte[])r[0], $"attachment|{messageId}"), WireJson.Options)!;
        return (pointer, Enum.Parse<AttachmentState>(r.GetString(1)), r.IsDBNull(2) ? null : r.GetString(2));
    }

    public List<long> AttachmentsToDownload()
    {
        using var cmd = Cmd("SELECT message_id FROM attachments WHERE state = $s ORDER BY message_id", ("$s", nameof(AttachmentState.Downloading)));
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    /// <summary>Saves a file in the profile's media folder, encrypted under the master key. Returns its name.</summary>
    public string WriteMedia(byte[] plaintext)
    {
        var name = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var tmp = Path.Combine(_mediaDir, name + ".tmp");
        File.WriteAllBytes(tmp, _cipher.Seal(plaintext, $"media|{name}"));
        File.Move(tmp, Path.Combine(_mediaDir, name));
        return name;
    }

    public byte[] ReadMedia(string name) => _cipher.Open(File.ReadAllBytes(Path.Combine(_mediaDir, name)), $"media|{name}");

    public void DeleteMedia(string name) => File.Delete(Path.Combine(_mediaDir, name));

    /// <summary>Sets (or with null, removes) one person's reaction to a message.</summary>
    public void SetReaction(long messageId, string reactor, string? emoji)
    {
        if (emoji is null) Exec("DELETE FROM reactions WHERE message_id = $i AND reactor = $r", ("$i", messageId), ("$r", reactor));
        else Exec("INSERT OR REPLACE INTO reactions VALUES ($i, $r, $e)", ("$i", messageId), ("$r", reactor), ("$e", emoji));
    }

    // ------------------------------------------------------------ outbox
    public void PutOutbox(long messageId, string peer, EnvelopeDto env) =>
        Exec("INSERT INTO outbox VALUES ($i, $p, $e)",
            ("$i", messageId), ("$p", peer), ("$e", _cipher.Seal(JsonSerializer.SerializeToUtf8Bytes(env, WireJson.Options), $"outbox|{messageId}")));

    public List<(long MessageId, string Peer, EnvelopeDto Envelope)> Outbox()
    {
        using var cmd = Cmd("SELECT message_id, peer, envelope FROM outbox ORDER BY message_id");
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, EnvelopeDto)>();
        while (r.Read())
        {
            var id = r.GetInt64(0);
            list.Add((id, r.GetString(1), JsonSerializer.Deserialize<EnvelopeDto>(_cipher.Open((byte[])r[2], $"outbox|{id}"), WireJson.Options)!));
        }
        return list;
    }

    public void DeleteOutbox(long messageId) => Exec("DELETE FROM outbox WHERE message_id = $i", ("$i", messageId));

    /// <summary>Copies of one group message still to deliver, one per member.</summary>
    public void PutGroupOutbox(long messageId, string peer, EnvelopeDto env) =>
        Exec("INSERT INTO group_outbox VALUES ($i, $p, $e)", ("$i", messageId), ("$p", peer),
            ("$e", _cipher.Seal(JsonSerializer.SerializeToUtf8Bytes(env, WireJson.Options), $"group_outbox|{messageId}|{peer}")));

    public List<(string Peer, EnvelopeDto Envelope)> GroupOutbox(long messageId)
    {
        using var cmd = Cmd("SELECT peer, envelope FROM group_outbox WHERE message_id = $i ORDER BY peer", ("$i", messageId));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, EnvelopeDto)>();
        while (r.Read())
        {
            var peer = r.GetString(0);
            list.Add((peer, JsonSerializer.Deserialize<EnvelopeDto>(_cipher.Open((byte[])r[1], $"group_outbox|{messageId}|{peer}"), WireJson.Options)!));
        }
        return list;
    }

    public List<long> GroupOutboxMessages()
    {
        using var cmd = Cmd("SELECT DISTINCT message_id FROM group_outbox ORDER BY message_id");
        using var r = cmd.ExecuteReader();
        var list = new List<long>();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    public void DeleteGroupOutbox(long messageId, string peer) =>
        Exec("DELETE FROM group_outbox WHERE message_id = $i AND peer = $p", ("$i", messageId), ("$p", peer));

    // ------------------------------------------------------------ groups
    public Group? GetGroup(string id)
    {
        var all = ReadGroups("WHERE id = $i", id);
        return all.Count == 1 ? all[0] : null;
    }

    public List<Group> Groups() => ReadGroups("", null);

    private List<Group> ReadGroups(string where, string? id)
    {
        var rows = new List<(string Id, byte[] Name, long Created, bool Active)>();
        using (var cmd = Cmd($"SELECT id, name, created_at, active FROM groups {where} ORDER BY created_at", ("$i", id)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) rows.Add((r.GetString(0), (byte[])r[1], r.GetInt64(2), r.GetInt64(3) == 1));

        var list = new List<Group>();
        foreach (var g in rows)
        {
            var members = new List<string>();
            using (var cmd = Cmd("SELECT username FROM group_members WHERE group_id = $i ORDER BY username", ("$i", g.Id)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) members.Add(r.GetString(0));
            list.Add(new Group(g.Id, Encoding.UTF8.GetString(_cipher.Open(g.Name, $"group|{g.Id}")), members, FromMs(g.Created), g.Active));
        }
        return list;
    }

    /// <summary>Writes the group and replaces its member list.</summary>
    public void PutGroup(Group g)
    {
        Exec("INSERT OR REPLACE INTO groups VALUES ($i, $n, $t, $a)", ("$i", g.Id),
            ("$n", _cipher.Seal(Encoding.UTF8.GetBytes(g.Name), $"group|{g.Id}")), ("$t", Ms(g.CreatedAt)), ("$a", g.Active ? 1 : 0));
        Exec("DELETE FROM group_members WHERE group_id = $i", ("$i", g.Id));
        foreach (var u in g.Members) Exec("INSERT INTO group_members VALUES ($i, $u)", ("$i", g.Id), ("$u", u));
    }

    // ------------------------------------------------------------ processed inbox ids
    public bool IsProcessed(long serverId)
    {
        using var cmd = Cmd("SELECT 1 FROM processed WHERE server_id = $i", ("$i", serverId));
        return cmd.ExecuteScalar() is not null;
    }

    public void MarkProcessed(long serverId, string outcome) =>
        Exec("INSERT OR REPLACE INTO processed VALUES ($i, $o)", ("$i", serverId), ("$o", outcome));
}
