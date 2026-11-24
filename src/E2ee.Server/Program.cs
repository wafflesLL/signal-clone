// Local demonstration relay. No client secrets are accepted or stored: it keeps public
// prekey bundles and forwards opaque envelopes.
//
// Run: dotnet run --project src/E2ee.Server --urls http://127.0.0.1:8000
// The SQLite path comes from the E2EE_DB setting (env var or --E2EE_DB=...), default relay.sqlite3.

using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var dbPath = app.Configuration["E2EE_DB"] ?? "relay.sqlite3";

SqliteConnection Open()
{
    var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS bundles (user TEXT PRIMARY KEY, payload TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS inbox (id INTEGER PRIMARY KEY AUTOINCREMENT, recipient TEXT NOT NULL, payload TEXT NOT NULL);
        """;
    cmd.ExecuteNonQuery();
    return conn;
}

static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object Value)[] args)
{
    var cmd = c.CreateCommand();
    cmd.CommandText = sql;
    foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
    return cmd;
}

// Body shape is {"data": {...}}; anything else is rejected.
static JsonObject? Data(JsonObject? body) => body?["data"] as JsonObject;

app.MapPost("/users/{user}/bundle", (string user, JsonObject? body) =>
{
    if (Data(body) is not { } data) return Results.BadRequest(new { error = "body must be {\"data\": {...}}" });
    using var c = Open();
    using var cmd = Command(c, "INSERT OR REPLACE INTO bundles VALUES ($u, $p)", ("$u", user), ("$p", data.ToJsonString()));
    cmd.ExecuteNonQuery();
    return Results.Ok(new { ok = true });
});

// Hands out the bundle with at most one one-time prekey, and removes that OPK so it is never reused.
app.MapGet("/users/{user}/bundle", (string user) =>
{
    using var c = Open();
    using var tx = c.BeginTransaction();
    using var select = Command(c, "SELECT payload FROM bundles WHERE user=$u", ("$u", user));
    if (select.ExecuteScalar() is not string payload) return Results.NotFound(new { error = "unknown user" });

    var data = JsonNode.Parse(payload)!.AsObject();
    JsonNode? opk = null;
    if (data["opks"] is JsonArray { Count: > 0 } opks)
    {
        opk = opks[0];
        opks.RemoveAt(0);
    }
    using var update = Command(c, "UPDATE bundles SET payload=$p WHERE user=$u", ("$p", data.ToJsonString()), ("$u", user));
    update.ExecuteNonQuery();
    tx.Commit();

    data.Remove("opks");
    data["opk"] = opk;
    return Results.Ok(new JsonObject { ["data"] = data });
});

app.MapPost("/users/{user}/messages", (string user, JsonObject? body) =>
{
    if (Data(body) is not { } data) return Results.BadRequest(new { error = "body must be {\"data\": {...}}" });
    using var c = Open();
    using var cmd = Command(c, "INSERT INTO inbox(recipient, payload) VALUES ($u, $p)", ("$u", user), ("$p", data.ToJsonString()));
    cmd.ExecuteNonQuery();
    return Results.Ok(new { ok = true });
});

// Returns and deletes every queued envelope for the user, oldest first.
app.MapGet("/users/{user}/messages", (string user) =>
{
    using var c = Open();
    using var tx = c.BeginTransaction();
    var messages = new JsonArray();
    using (var select = Command(c, "SELECT payload FROM inbox WHERE recipient=$u ORDER BY id", ("$u", user)))
    using (var reader = select.ExecuteReader())
        while (reader.Read()) messages.Add(JsonNode.Parse(reader.GetString(0)));
    using var delete = Command(c, "DELETE FROM inbox WHERE recipient=$u", ("$u", user));
    delete.ExecuteNonQuery();
    tx.Commit();
    return Results.Ok(new JsonObject { ["messages"] = messages });
});

app.Run();

public partial class Program; // exposed for WebApplicationFactory in tests
