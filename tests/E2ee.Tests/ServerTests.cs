using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace E2ee.Tests;

public sealed class ServerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"e2ee-test-{Guid.NewGuid():N}.sqlite3");
    private readonly WebApplicationFactory<Program> _factory;

    public ServerTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("E2EE_DB", _dbPath));
    }

    public void Dispose()
    {
        _factory.Dispose();
        File.Delete(_dbPath);
    }

    private static JsonObject Data(string json) => new() { ["data"] = JsonNode.Parse(json) };

    [Fact]
    public async Task Relay_HandsOutEachOpkOnceAndDrainsInbox()
    {
        var c = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/users/bob/bundle", Data("""{"name":"public","opks":[{"id":1},{"id":2}]}"""))).StatusCode);

        async Task<JsonNode?> Opk() => (await c.GetFromJsonAsync<JsonObject>("/users/bob/bundle"))!["data"]!["opk"];
        Assert.Equal(1, (int)(await Opk())!["id"]!);
        Assert.Equal(2, (int)(await Opk())!["id"]!);
        Assert.Null(await Opk());

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/users/bob/messages", Data("""{"ciphertext":"opaque"}"""))).StatusCode);
        var first = (await c.GetFromJsonAsync<JsonObject>("/users/bob/messages"))!["messages"]!.AsArray();
        Assert.Single(first);
        Assert.Equal("opaque", (string)first[0]!["ciphertext"]!);
        Assert.Empty((await c.GetFromJsonAsync<JsonObject>("/users/bob/messages"))!["messages"]!.AsArray());
    }

    [Fact]
    public async Task Relay_UnknownUserIs404_AndBadBodyIs400()
    {
        var c = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/users/nobody/bundle")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/users/bob/messages", new JsonObject { ["x"] = 1 })).StatusCode);
    }
}
