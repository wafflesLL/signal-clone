// Headless CLI over E2ee.Messaging. Each profile is one user with its own encrypted state.
//
//   dotnet run --project src/E2ee.Client -- --profile alice register alice --server http://127.0.0.1:8000
//   dotnet run --project src/E2ee.Client -- --profile alice chat bob

using E2ee.Messaging;

const string Usage = """
    usage: e2ee [--profile NAME | --data-dir DIR] [--server URL] [--key-file] <command>

    commands:
      register <username>        create keys and claim a username on the relay
      whoami                     show this profile's username and relay
      add <username>             look up a user and remember their identity key
      contacts                   list contacts
      send <username> <text...>  send one message
      sync                       fetch and print new messages
      history <username> [n]     show the last n messages (default 20)
      trust <username>           accept a contact's new identity key (check with them first)
      chat <username>            interactive chat; type /quit to leave

    options:
      --profile NAME   profile under the user data folder (default: default)
      --data-dir DIR   use this folder for the profile instead
      --server URL     relay URL (needed for register; remembered afterwards)
      --key-file       keep the master key in a file instead of the OS keychain
    """;

string? profile = null, dataDir = null, server = null;
var keyFile = Environment.GetEnvironmentVariable("E2EE_KEYSTORE") == "file";
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--profile" when i + 1 < args.Length: profile = args[++i]; break;
        case "--data-dir" when i + 1 < args.Length: dataDir = args[++i]; break;
        case "--server" when i + 1 < args.Length: server = args[++i]; break;
        case "--key-file": keyFile = true; break;
        case "-h" or "--help": Console.WriteLine(Usage); return 0;
        default: rest.Add(args[i]); break;
    }
}
if (rest.Count == 0) { Console.Error.WriteLine(Usage); return 2; }

dataDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "E2ee", profile ?? "default");
var command = rest[0];
var argv = rest.Skip(1).ToList();

string Arg(int i, string name) => i < argv.Count ? argv[i] : throw new MessengerException($"missing <{name}>; see --help");

try
{
    using var m = Messenger.Open(new MessengerOptions
    {
        DataDirectory = dataDir,
        Server = server is null ? null : new Uri(server),
        KeyStore = keyFile ? new FileMasterKeyStore(dataDir) : null,
    });
    switch (command)
    {
        case "register":
            await m.RegisterAsync(Arg(0, "username"));
            Console.WriteLine($"Registered as {m.Username} on {m.Server}");
            WarnIfPlainHttp(m.Server);
            break;

        case "whoami":
            Console.WriteLine(m.IsRegistered ? $"{m.Username} on {m.Server} (profile {dataDir})" : $"Not registered (profile {dataDir})");
            break;

        case "add":
            var added = await m.AddContactAsync(Arg(0, "username"));
            Console.WriteLine($"Added {added.Username}");
            break;

        case "contacts":
            foreach (var c in m.Contacts()) Console.WriteLine(c.Username);
            break;

        case "send":
            var sent = await m.SendAsync(Arg(0, "username"), string.Join(' ', argv.Skip(1)));
            Console.WriteLine(sent.Status == MessageStatus.Sent ? "Sent." : "Relay unreachable; queued and will retry on the next sync.");
            break;

        case "sync":
            var result = await m.SyncAsync();
            foreach (var msg in result.Received) PrintMessage(msg);
            foreach (var p in result.Problems) Console.Error.WriteLine($"! {p}");
            if (result.Received.Count == 0) Console.WriteLine("No new messages.");
            break;

        case "history":
            var n = argv.Count > 1 && int.TryParse(argv[1], out var parsed) ? parsed : 20;
            foreach (var msg in m.History(Arg(0, "username"), n)) PrintMessage(msg);
            break;

        case "trust":
            await m.TrustNewIdentityAsync(Arg(0, "username"));
            Console.WriteLine($"Now trusting {argv[0]}'s current identity key. Old sessions were dropped.");
            break;

        case "chat":
            await Chat(m, Arg(0, "username"));
            break;

        default:
            Console.Error.WriteLine($"unknown command '{command}'\n\n{Usage}");
            return 2;
    }
    return 0;
}
catch (Exception e) when (e is MessengerException or RelayException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static void WarnIfPlainHttp(Uri server)
{
    if (server.Scheme == "http" && !server.IsLoopback)
        Console.Error.WriteLine("warning: the relay uses plain HTTP. Messages stay end-to-end encrypted, but anyone on the network can see who talks to whom.");
}

static void PrintMessage(StoredMessage msg)
{
    var who = msg.Outgoing ? "me" : msg.Peer;
    var flag = msg.Status switch { MessageStatus.Pending => " (pending)", MessageStatus.Failed => " (failed)", _ => "" };
    Console.WriteLine($"[{msg.SentAt.ToLocalTime():HH:mm}] {who}: {msg.Text}{flag}");
}

static async Task Chat(Messenger m, string peer)
{
    var consoleLock = new object();
    void Print(string line) { lock (consoleLock) Console.WriteLine(line); }

    if (m.GetContact(peer) is null) await m.AddContactAsync(peer);
    foreach (var msg in m.History(peer, 20)) PrintMessage(msg);
    Print($"--- chatting with {peer} as {m.Username}. Type /quit to leave. ---");

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var relayDown = false;

    var poller = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var result = await m.SyncAsync(cts.Token);
                if (relayDown) { Print("--- relay is back ---"); relayDown = false; }
                foreach (var msg in result.Received)
                    Print(msg.Peer == peer ? $"[{msg.SentAt.ToLocalTime():HH:mm}] {msg.Peer}: {msg.Text}" : $"(new message from {msg.Peer}; run: chat {msg.Peer})");
                foreach (var p in result.Problems) Print($"! {p}");
            }
            catch (RelayException) when (!cts.IsCancellationRequested)
            {
                if (!relayDown) Print("--- relay unreachable, retrying ---");
                relayDown = true;
            }
            catch (OperationCanceledException) { break; }
            try { await Task.Delay(TimeSpan.FromSeconds(2), cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    });

    while (!cts.IsCancellationRequested)
    {
        var line = await Task.Run(Console.ReadLine);
        if (line is null || line.Trim() == "/quit") break;
        if (string.IsNullOrWhiteSpace(line)) continue;
        try
        {
            var sent = await m.SendAsync(peer, line);
            if (sent.Status == MessageStatus.Pending) Print("(queued: relay unreachable)");
        }
        catch (MessengerException e) { Print($"! {e.Message}"); }
        catch (RelayException e) { Print($"! {e.Message}"); }
    }
    cts.Cancel();
    await poller;
}
