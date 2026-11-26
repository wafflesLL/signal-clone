using E2ee.Messaging;
using Microsoft.AspNetCore.Mvc.Testing;

namespace E2ee.Tests;

/// <summary>An in-process relay with a throwaway database, plus helpers to open profiles against it.</summary>
public sealed class RelayFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"e2ee-test-{Guid.NewGuid():N}");
    private readonly List<Messenger> _open = new();
    public WebApplicationFactory<Program> Factory { get; }
    public string RelayDbPath => Path.Combine(_root, "relay.sqlite3");

    public RelayFixture()
    {
        Directory.CreateDirectory(_root);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("E2EE_DB", RelayDbPath));
    }

    public string ProfileDir(string name) => Path.Combine(_root, "profiles", name);

    public Messenger Open(string profile, PrekeyPolicy? prekeys = null)
    {
        var m = Messenger.Open(new MessengerOptions
        {
            DataDirectory = ProfileDir(profile),
            HttpClient = Factory.CreateClient(),
            KeyStore = new InMemoryMasterKeyStore(ProfileDir(profile)),
            Prekeys = prekeys ?? new PrekeyPolicy(),
        });
        _open.Add(m);
        return m;
    }

    /// <summary>
    /// Registers a profile. Unless <paramref name="friends"/> is false it is made friends
    /// with every profile still open, so tests about messaging don't each need the request
    /// round trip (which <see cref="Befriend"/> and the request tests exercise for real).
    /// </summary>
    public async Task<Messenger> Register(string name, PrekeyPolicy? prekeys = null, bool friends = true)
    {
        var m = Open(name, prekeys);
        await m.RegisterAsync(name);
        if (friends)
            foreach (var other in _open.Where(o => o != m && o.IsRegistered && !_strangers.Contains(o)).ToList())
            {
                await m.MakeFriendForTestAsync(other.Username!);
                await other.MakeFriendForTestAsync(name);
            }
        else _strangers.Add(m);
        return m;
    }

    private readonly HashSet<Messenger> _strangers = new();

    /// <summary>The real way: <paramref name="from"/> sends a request and <paramref name="to"/> accepts it.</summary>
    public static async Task Befriend(Messenger from, Messenger to)
    {
        await from.AddContactAsync(to.Username!);
        await to.SyncAsync();
        await to.AcceptRequestAsync(from.Username!);
        await from.SyncAsync();
    }

    /// <summary>Simulates quitting the app: closes the profile so it can be reopened.</summary>
    public void Close(Messenger m)
    {
        _open.Remove(m);
        m.Dispose();
    }

    public void Dispose()
    {
        foreach (var m in _open) m.Dispose();
        Factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
