// SQLite storage for the relay. Holds only public keys and opaque envelopes.

using E2ee.Wire;
using Microsoft.Data.Sqlite;

namespace E2ee.Server;

public enum RegisterOutcome { Created, AlreadyRegistered, Conflict }

public sealed class RelayStore
{
    public const int MaxOneTimePrekeys = 1000;
    public const int MaxInboxPerUser = 5000;
    public const int FetchBatch = 100;

    private readonly string _connectionString;

    public RelayStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS accounts (
                username TEXT PRIMARY KEY, sig_pub BLOB NOT NULL, dh_pub BLOB NOT NULL, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS signed_prekeys (
                username TEXT PRIMARY KEY, id INTEGER NOT NULL, pub BLOB NOT NULL, sig BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS one_time_prekeys (
                username TEXT NOT NULL, id INTEGER NOT NULL, pub BLOB NOT NULL, PRIMARY KEY (username, id));
            CREATE TABLE IF NOT EXISTS inbox (
                id INTEGER PRIMARY KEY AUTOINCREMENT, recipient TEXT NOT NULL, sender TEXT NOT NULL,
                received_at INTEGER NOT NULL, envelope TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS inbox_recipient ON inbox (recipient, id);
            CREATE TABLE IF NOT EXISTS attachments (
                id TEXT PRIMARY KEY, uploader TEXT NOT NULL, size INTEGER NOT NULL, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS attachment_recipients (
                attachment_id TEXT NOT NULL, username TEXT NOT NULL, fetched INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (attachment_id, username));
            """);
        AttachmentDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "relay-attachments");
        Directory.CreateDirectory(AttachmentDir);
    }

    /// <summary>Where encrypted attachment blobs live, one file per attachment, until fetched.</summary>
    public string AttachmentDir { get; }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = Cmd(c, null, sql);
        cmd.ExecuteNonQuery();
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public IdentityResponse? GetIdentity(string user)
    {
        using var c = Open();
        using var cmd = Cmd(c, null, "SELECT sig_pub, dh_pub FROM accounts WHERE username = $u", ("$u", user));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new IdentityResponse(user, (byte[])r[0], (byte[])r[1]) : null;
    }

    /// <summary>Trust on first use: the first key to claim a username owns it.</summary>
    public RegisterOutcome Register(RegisterRequest req)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Cmd(c, tx, "SELECT sig_pub, dh_pub FROM accounts WHERE username = $u", ("$u", req.Username)))
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
                return ((byte[])r[0]).AsSpan().SequenceEqual(req.IdentitySigPub) && ((byte[])r[1]).AsSpan().SequenceEqual(req.IdentityDhPub)
                    ? RegisterOutcome.AlreadyRegistered
                    : RegisterOutcome.Conflict;
        }
        using (var insert = Cmd(c, tx, "INSERT INTO accounts VALUES ($u, $s, $d, $t)",
                   ("$u", req.Username), ("$s", req.IdentitySigPub), ("$d", req.IdentityDhPub), ("$t", Now())))
            insert.ExecuteNonQuery();
        tx.Commit();
        return RegisterOutcome.Created;
    }

    /// <summary>Returns false if the upload would exceed <see cref="MaxOneTimePrekeys"/>.</summary>
    public bool UploadKeys(string user, UploadKeysRequest req)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var count = Cmd(c, tx, "SELECT COUNT(*) FROM one_time_prekeys WHERE username = $u", ("$u", user)))
            if ((long)count.ExecuteScalar()! + req.OneTimePrekeys.Count > MaxOneTimePrekeys) return false;
        if (req.SignedPrekey is { } spk)
        {
            using var cmd = Cmd(c, tx, "INSERT OR REPLACE INTO signed_prekeys VALUES ($u, $i, $p, $s)",
                ("$u", user), ("$i", (long)spk.Id), ("$p", spk.Pub), ("$s", spk.Sig));
            cmd.ExecuteNonQuery();
        }
        foreach (var opk in req.OneTimePrekeys)
        {
            using var cmd = Cmd(c, tx, "INSERT OR REPLACE INTO one_time_prekeys VALUES ($u, $i, $p)",
                ("$u", user), ("$i", (long)opk.Id), ("$p", opk.Pub));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public PrekeyStatusResponse PrekeyStatus(string user)
    {
        using var c = Open();
        using var count = Cmd(c, null, "SELECT COUNT(*) FROM one_time_prekeys WHERE username = $u", ("$u", user));
        using var spk = Cmd(c, null, "SELECT id FROM signed_prekeys WHERE username = $u", ("$u", user));
        return new PrekeyStatusResponse((int)(long)count.ExecuteScalar()!, spk.ExecuteScalar() is long id ? (uint)id : null);
    }

    /// <summary>Returns the bundle and deletes the one-time prekey it hands out, so it is never reused.</summary>
    public BundleResponse? TakeBundle(string user)
    {
        var identity = GetIdentity(user);
        if (identity is null) return null;
        using var c = Open();
        using var tx = c.BeginTransaction();
        SignedPrekeyDto spk;
        using (var cmd = Cmd(c, tx, "SELECT id, pub, sig FROM signed_prekeys WHERE username = $u", ("$u", user)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            spk = new SignedPrekeyDto((uint)r.GetInt64(0), (byte[])r[1], (byte[])r[2]);
        }
        OneTimePrekeyDto? opk = null;
        using (var cmd = Cmd(c, tx, "SELECT id, pub FROM one_time_prekeys WHERE username = $u ORDER BY id LIMIT 1", ("$u", user)))
        using (var r = cmd.ExecuteReader())
            if (r.Read()) opk = new OneTimePrekeyDto((uint)r.GetInt64(0), (byte[])r[1]);
        if (opk is not null)
        {
            using var del = Cmd(c, tx, "DELETE FROM one_time_prekeys WHERE username = $u AND id = $i", ("$u", user), ("$i", (long)opk.Id));
            del.ExecuteNonQuery();
        }
        tx.Commit();
        return new BundleResponse(user, identity.IdentitySigPub, identity.IdentityDhPub, spk, opk);
    }

    /// <summary>Returns the new inbox id, or null if the recipient's inbox is full.</summary>
    public long? Enqueue(string recipient, string sender, string envelopeJson)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var count = Cmd(c, tx, "SELECT COUNT(*) FROM inbox WHERE recipient = $u", ("$u", recipient)))
            if ((long)count.ExecuteScalar()! >= MaxInboxPerUser) return null;
        using var cmd = Cmd(c, tx, "INSERT INTO inbox (recipient, sender, received_at, envelope) VALUES ($r, $s, $t, $e) RETURNING id",
            ("$r", recipient), ("$s", sender), ("$t", Now()), ("$e", envelopeJson));
        var id = (long)cmd.ExecuteScalar()!;
        tx.Commit();
        return id;
    }

    /// <summary>Oldest first. Messages stay queued until the recipient acknowledges them.</summary>
    public List<(long Id, string Sender, long ReceivedAt, string Envelope)> Fetch(string user)
    {
        using var c = Open();
        using var cmd = Cmd(c, null, $"SELECT id, sender, received_at, envelope FROM inbox WHERE recipient = $u ORDER BY id LIMIT {FetchBatch}", ("$u", user));
        using var r = cmd.ExecuteReader();
        var items = new List<(long, string, long, string)>();
        while (r.Read()) items.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));
        return items;
    }

    public bool HasMessagesAfter(string user, long afterId)
    {
        using var c = Open();
        using var cmd = Cmd(c, null, "SELECT 1 FROM inbox WHERE recipient = $u AND id > $a LIMIT 1", ("$u", user), ("$a", afterId));
        return cmd.ExecuteScalar() is not null;
    }

    // ------------------------------------------------------------ attachments
    // Opaque encrypted blobs. Only the recipients named at upload may fetch one, and it is
    // deleted once all of them confirm they have it, or after Attachments.Retention.

    private string BlobPath(string id) => Path.Combine(AttachmentDir, id);

    public string PutAttachment(string uploader, IReadOnlyCollection<string> recipients, byte[] blob)
    {
        PurgeExpiredAttachments();
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        File.WriteAllBytes(BlobPath(id), blob);
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Cmd(c, tx, "INSERT INTO attachments VALUES ($i, $u, $s, $t)", ("$i", id), ("$u", uploader), ("$s", (long)blob.Length), ("$t", Now())))
            cmd.ExecuteNonQuery();
        foreach (var r in recipients)
        {
            using var cmd = Cmd(c, tx, "INSERT OR IGNORE INTO attachment_recipients (attachment_id, username) VALUES ($i, $u)", ("$i", id), ("$u", r));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }

    /// <summary>The blob, if it exists and <paramref name="user"/> is one of its recipients.</summary>
    public byte[]? GetAttachment(string user, string id)
    {
        using var c = Open();
        using var cmd = Cmd(c, null, "SELECT 1 FROM attachment_recipients WHERE attachment_id = $i AND username = $u", ("$i", id), ("$u", user));
        if (cmd.ExecuteScalar() is null) return null;
        try { return File.ReadAllBytes(BlobPath(id)); }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>Records that the user has the attachment; deletes it once every recipient does.</summary>
    public bool AckAttachment(string user, string id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var cmd = Cmd(c, tx, "UPDATE attachment_recipients SET fetched = 1 WHERE attachment_id = $i AND username = $u", ("$i", id), ("$u", user)))
            if (cmd.ExecuteNonQuery() == 0) return false;
        bool done;
        using (var cmd = Cmd(c, tx, "SELECT 1 FROM attachment_recipients WHERE attachment_id = $i AND fetched = 0", ("$i", id)))
            done = cmd.ExecuteScalar() is null;
        if (done) DeleteAttachment(c, tx, id);
        tx.Commit();
        return true;
    }

    public bool AttachmentExists(string id) => File.Exists(BlobPath(id));

    private void PurgeExpiredAttachments()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var expired = new List<string>();
        using (var cmd = Cmd(c, tx, "SELECT id FROM attachments WHERE created_at < $t", ("$t", Now() - (long)Attachments.Retention.TotalMilliseconds)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) expired.Add(r.GetString(0));
        foreach (var id in expired) DeleteAttachment(c, tx, id);
        tx.Commit();
    }

    private void DeleteAttachment(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using (var cmd = Cmd(c, tx, "DELETE FROM attachment_recipients WHERE attachment_id = $i", ("$i", id))) cmd.ExecuteNonQuery();
        using (var cmd = Cmd(c, tx, "DELETE FROM attachments WHERE id = $i", ("$i", id))) cmd.ExecuteNonQuery();
        File.Delete(BlobPath(id));
    }

    public void Ack(string user, IEnumerable<long> ids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = Cmd(c, tx, "DELETE FROM inbox WHERE recipient = $u AND id = $i", ("$u", user), ("$i", id));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
