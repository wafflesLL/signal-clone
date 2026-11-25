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
            """);
    }

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
