// The profile's local SQLite database. Private keys, ratchet state, message bodies, and
// queued envelopes are encrypted with the master key; usernames, contact public keys,
// and timestamps are not (they are metadata the relay sees anyway).

using System.Text;
using System.Text.Json;
using E2ee.Wire;
using Microsoft.Data.Sqlite;

namespace E2ee.Messaging;

public enum MessageStatus { Pending, Sent, Received, Failed }

public sealed record StoredMessage(long Id, string Peer, bool Outgoing, string Text, DateTimeOffset SentAt, MessageStatus Status);

public sealed record Contact(string Username, byte[] IdentitySigPub, byte[] IdentityDhPub, DateTimeOffset AddedAt);

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

    public LocalStore(string dbPath, byte[] masterKey)
    {
        _cipher = new StateCipher(masterKey);
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
            """);
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
    public Contact? GetContact(string username)
    {
        using var cmd = Cmd("SELECT sig_pub, dh_pub, added_at FROM contacts WHERE username = $u", ("$u", username));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new Contact(username, (byte[])r[0], (byte[])r[1], FromMs(r.GetInt64(2))) : null;
    }

    public void PutContact(Contact c) =>
        Exec("INSERT OR REPLACE INTO contacts VALUES ($u, $s, $d, $t)",
            ("$u", c.Username), ("$s", c.IdentitySigPub), ("$d", c.IdentityDhPub), ("$t", Ms(c.AddedAt)));

    public List<Contact> Contacts()
    {
        using var cmd = Cmd("SELECT username, sig_pub, dh_pub, added_at FROM contacts ORDER BY username");
        using var r = cmd.ExecuteReader();
        var list = new List<Contact>();
        while (r.Read()) list.Add(new Contact(r.GetString(0), (byte[])r[1], (byte[])r[2], FromMs(r.GetInt64(3))));
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
    public long InsertMessage(string peer, bool outgoing, string text, DateTimeOffset sentAt, MessageStatus status)
    {
        using var cmd = Cmd("INSERT INTO messages (peer, outgoing, body, sent_at, status) VALUES ($p, $o, x'', $t, $s) RETURNING id",
            ("$p", peer), ("$o", outgoing ? 1 : 0), ("$t", Ms(sentAt)), ("$s", status.ToString()));
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
        return new StoredMessage(id, r.GetString(1), r.GetInt64(2) == 1, text, FromMs(r.GetInt64(4)), Enum.Parse<MessageStatus>(r.GetString(5)));
    }

    public StoredMessage GetMessage(long id)
    {
        using var cmd = Cmd("SELECT id, peer, outgoing, body, sent_at, status FROM messages WHERE id = $i", ("$i", id));
        using var r = cmd.ExecuteReader();
        r.Read();
        return ReadMessage(r);
    }

    /// <summary>The newest <paramref name="limit"/> messages with the peer, oldest first.</summary>
    public List<StoredMessage> History(string peer, int limit)
    {
        using var cmd = Cmd("""
            SELECT * FROM (SELECT id, peer, outgoing, body, sent_at, status FROM messages WHERE peer = $p ORDER BY id DESC LIMIT $n)
            ORDER BY id
            """, ("$p", peer), ("$n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<StoredMessage>();
        while (r.Read()) list.Add(ReadMessage(r));
        return list;
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

    // ------------------------------------------------------------ processed inbox ids
    public bool IsProcessed(long serverId)
    {
        using var cmd = Cmd("SELECT 1 FROM processed WHERE server_id = $i", ("$i", serverId));
        return cmd.ExecuteScalar() is not null;
    }

    public void MarkProcessed(long serverId, string outcome) =>
        Exec("INSERT OR REPLACE INTO processed VALUES ($i, $o)", ("$i", serverId), ("$o", outcome));
}
