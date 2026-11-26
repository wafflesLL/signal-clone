// Desktop app entry point.
//
//   dotnet run --project src/E2ee.App
//   dotnet run --project src/E2ee.App -- --profile bob      (a second person on the same machine)
//
// Options: --profile NAME, --data-dir DIR, --server URL (pre-fills sign-up), --key-file.
// Sign-up defaults to the relay baked in at build time (DefaultRelay in E2ee.App.csproj);
// the E2EE_SERVER environment variable or --server overrides it.

using Avalonia;
using E2ee.Messaging;

namespace E2ee.App;

public sealed record AppOptions(string DataDirectory, bool KeyFile, string DefaultServer)
{
    public static AppOptions Current { get; set; } = Parse([]);

    public static string DefaultProfilesRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "E2ee");

    public static AppOptions Parse(string[] args) => Parse(args, DefaultProfilesRoot);

    internal static AppOptions Parse(string[] args, string profilesRoot)
    {
        string? profile = null;
        string server = Environment.GetEnvironmentVariable("E2EE_SERVER") is { Length: > 0 } env ? env : BuiltInRelay;
        string? dataDir = null;
        var keyFile = Environment.GetEnvironmentVariable("E2EE_KEYSTORE") == "file";
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Length: profile = args[++i]; break;
                case "--data-dir" when i + 1 < args.Length: dataDir = args[++i]; break;
                case "--server" when i + 1 < args.Length: server = args[++i]; break;
                case "--key-file": keyFile = true; break;
            }
        }
        // An explicit --data-dir is a one-off folder: no switching between accounts there.
        if (dataDir is not null) return new AppOptions(dataDir, keyFile, server);
        var root = profilesRoot;
        profile ??= LastProfile(root) ?? "default";
        return new AppOptions(Path.Combine(root, profile), keyFile, server) { ProfilesRoot = root };
    }

    // ------------------------------------------------------------ accounts on this computer
    //
    // Each account is a folder under ProfilesRoot. Its keys and history are encrypted, so a
    // small account.json next to them names the account for the "use another account" list.

    /// <summary>The folder holding every account on this computer; null when using a single --data-dir.</summary>
    public string? ProfilesRoot { get; init; }

    public bool CanSwitchAccounts => ProfilesRoot is not null;

    private const string AccountFile = "account.json", LastProfileFile = "last-account";

    public sealed record LocalAccount(string Profile, string Title, bool Locked, AppOptions Options);

    /// <summary>Every finished account in <see cref="ProfilesRoot"/>, by name.</summary>
    public IReadOnlyList<LocalAccount> LocalAccounts()
    {
        if (ProfilesRoot is null || !Directory.Exists(ProfilesRoot)) return [];
        return Directory.GetDirectories(ProfilesRoot)
            .Select(dir => ForProfile(Path.GetFileName(dir)))
            .Where(o => o.HasProfile)
            .Select(o => new LocalAccount(Path.GetFileName(o.DataDirectory), o.AccountName ?? Path.GetFileName(o.DataDirectory), o.IsLocked, o))
            .OrderBy(a => a.Title, StringComparer.Ordinal)
            .ToList();
    }

    public AppOptions ForProfile(string profile) =>
        this with { DataDirectory = Path.Combine(ProfilesRoot ?? throw new InvalidOperationException("no profiles folder"), profile) };

    /// <summary>A fresh, empty folder for signing up a new account.</summary>
    public AppOptions ForNewAccount() =>
        ForProfile("account-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());

    /// <summary>The username this folder's account was registered as, if known.</summary>
    public string? AccountName
    {
        get
        {
            try
            {
                using var f = File.OpenRead(Path.Combine(DataDirectory, AccountFile));
                return System.Text.Json.JsonDocument.Parse(f).RootElement.GetProperty("username").GetString();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                          or KeyNotFoundException or InvalidOperationException) { return null; }
        }
    }

    /// <summary>Names the account (for the account list) and opens it next time the app starts.</summary>
    public void Remember(string username)
    {
        try
        {
            File.WriteAllText(Path.Combine(DataDirectory, AccountFile), System.Text.Json.JsonSerializer.Serialize(new { username }));
            if (ProfilesRoot is not null) File.WriteAllText(Path.Combine(ProfilesRoot, LastProfileFile), Path.GetFileName(DataDirectory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string? LastProfile(string root)
    {
        try
        {
            var name = File.ReadAllText(Path.Combine(root, LastProfileFile)).Trim();
            return name.Length > 0 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..")
                && Directory.Exists(Path.Combine(root, name)) ? name : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The relay this build signs up on by default.</summary>
    public static string BuiltInRelay { get; } =
        typeof(AppOptions).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DefaultRelay")?.Value is { Length: > 0 } v ? v : "http://127.0.0.1:8000";

    /// <summary>Test hooks: an in-process relay client and an in-memory key store.</summary>
    public Func<HttpClient>? HttpClientFactory { get; init; }
    public IMasterKeyStore? KeyStore { get; init; }

    /// <summary>Argon2id cost for the password lock; tests use a cheap one.</summary>
    public PasswordCost? PasswordCost { get; init; }

    /// <summary>With a password, the profile's master key is locked with it (see PasswordMasterKeyStore).</summary>
    public MessengerOptions ToMessengerOptions(Uri? server, string? password = null) => new()
    {
        DataDirectory = DataDirectory,
        Server = server,
        HttpClient = HttpClientFactory?.Invoke(),
        KeyStore = password is not null
            ? new PasswordMasterKeyStore(DataDirectory, password, PasswordCost)
            : KeyStore ?? (KeyFile ? new FileMasterKeyStore(DataDirectory) : null),
    };

    public bool HasProfile => File.Exists(Path.Combine(DataDirectory, "messenger.db"));
    public bool IsLocked => PasswordMasterKeyStore.IsLocked(DataDirectory);
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppOptions.Current = AppOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
