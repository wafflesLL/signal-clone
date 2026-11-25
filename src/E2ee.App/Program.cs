// Desktop app entry point.
//
//   dotnet run --project src/E2ee.App
//   dotnet run --project src/E2ee.App -- --profile bob      (a second person on the same machine)
//
// Options: --profile NAME, --data-dir DIR, --server URL (pre-fills sign-up), --key-file.

using Avalonia;
using E2ee.Messaging;

namespace E2ee.App;

public sealed record AppOptions(string DataDirectory, bool KeyFile, string DefaultServer)
{
    public static AppOptions Current { get; set; } = Parse([]);

    public static AppOptions Parse(string[] args)
    {
        string profile = "default", server = "http://127.0.0.1:8000";
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
        dataDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "E2ee", profile);
        return new AppOptions(dataDir, keyFile, server);
    }

    /// <summary>Test hooks: an in-process relay client and an in-memory key store.</summary>
    public Func<HttpClient>? HttpClientFactory { get; init; }
    public IMasterKeyStore? KeyStore { get; init; }

    public MessengerOptions ToMessengerOptions(Uri? server) => new()
    {
        DataDirectory = DataDirectory,
        Server = server,
        HttpClient = HttpClientFactory?.Invoke(),
        KeyStore = KeyStore ?? (KeyFile ? new FileMasterKeyStore(DataDirectory) : null),
    };
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
