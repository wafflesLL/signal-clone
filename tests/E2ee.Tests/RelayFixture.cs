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

    public async Task<Messenger> Register(string name, PrekeyPolicy? prekeys = null)
    {
        var m = Open(name, prekeys);
        await m.RegisterAsync(name);
        return m;
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
