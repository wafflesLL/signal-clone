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
      safety <username>          show the safety number to compare with a contact
      verify <username>          mark a contact verified after comparing safety numbers
      trust <username>           review and accept a contact's new identity key
      chat <username>            interactive chat; type /quit to leave

    groups (name a group as #name, or by the start of its id):
      groups                          list your groups and their members
      group-create <name> <users...>  start a group with these people
      group-add <#group> <users...>   add people to a group
      group-leave <#group>            leave a group (you keep its history)
      send <#group> <text...>         send to everyone in the group
      history <#group> [n]            show the group's last n messages
      chat <#group>                   interactive group chat

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
            var to = Arg(0, "username");
            var body = string.Join(' ', argv.Skip(1));
            var sent = IsGroupRef(to) ? await m.SendGroupAsync(FindGroup(m, to).Id, body) : await m.SendAsync(to, body);
            Console.WriteLine(sent.Status == MessageStatus.Sent ? "Sent." : "Relay unreachable; queued and will retry on the next sync.");
            break;

        case "sync":
            var result = await m.SyncAsync();
            foreach (var msg in result.Received) PrintMessage(msg, m);
            foreach (var p in result.Problems) Console.Error.WriteLine($"! {p}");
            foreach (var u in result.IdentityChanges) Console.Error.WriteLine($"! run 'trust {u}' after checking with them");
            if (result.Received.Count == 0) Console.WriteLine("No new messages.");
            break;

        case "history":
            var n = argv.Count > 1 && int.TryParse(argv[1], out var parsed) ? parsed : 20;
            var whose = Arg(0, "username");
            foreach (var msg in IsGroupRef(whose) ? m.GroupHistory(FindGroup(m, whose).Id, n) : m.History(whose, n)) PrintMessage(msg);
            break;

        case "groups":
            foreach (var g in m.Groups())
                Console.WriteLine($"#{g.Name}  ({g.Id[..8]}){(g.Active ? "" : "  [left]")}\n    you, {string.Join(", ", g.Members)}");
            if (m.Groups().Count == 0) Console.WriteLine("No groups. Start one with: group-create <name> <users...>");
            break;

        case "group-create":
        {
            var g = await m.CreateGroupAsync(Arg(0, "name"), argv.Skip(1).SelectMany(SplitUsers));
            Console.WriteLine($"Created #{g.Name} with {string.Join(", ", g.Members)}.");
            break;
        }

        case "group-add":
        {
            var g = await m.AddGroupMembersAsync(FindGroup(m, Arg(0, "#group")).Id, argv.Skip(1).SelectMany(SplitUsers));
            Console.WriteLine($"#{g.Name} is now: you, {string.Join(", ", g.Members)}.");
            break;
        }

        case "group-leave":
        {
            var g = await m.LeaveGroupAsync(FindGroup(m, Arg(0, "#group")).Id);
            Console.WriteLine($"You left #{g.Name}.");
            break;
        }

        case "safety":
        {
            var peer = Arg(0, "username");
            var c = m.GetContact(peer) ?? throw new MessengerException($"{peer} is not a contact; run: add {peer}");
            if (c.IdentityChanged) throw new IdentityChangedException(peer);
            PrintSafetyNumber(await m.SafetyNumberAsync(peer));
            Console.WriteLine(c.Verified ? $"{peer} is verified." : $"Not verified. After comparing with {peer}, run: verify {peer}");
            break;
        }

        case "verify":
        {
            var peer = Arg(0, "username");
            PrintSafetyNumber(await m.SafetyNumberAsync(peer));
            if (!Confirm($"Does {peer} see exactly these numbers?")) { Console.WriteLine("Not verified."); break; }
            await m.SetVerifiedAsync(peer, true);
            Console.WriteLine($"{peer} is verified.");
            break;
        }

        case "trust":
        {
            var peer = Arg(0, "username");
            var check = await m.CheckIdentityAsync(peer);
            if (check.MatchesTrustedKey && m.GetContact(peer) is { IdentityChanged: false })
            {
                Console.WriteLine($"{peer}'s key hasn't changed; nothing to do.");
                break;
            }
            Console.WriteLine($"Safety number for {peer}'s new key:");
            PrintSafetyNumber(check.SafetyNumber);
            Console.WriteLine($"Compare it with {peer} in person or on a call. If it doesn't match, someone may be impersonating them.");
            if (!Confirm("Trust this key?")) { Console.WriteLine("Not trusted. Their messages stay on hold."); break; }
            var verified = Confirm($"Did you compare it with {peer} and it matched (mark verified)?");
            await m.TrustIdentityAsync(check, verified);
            Console.WriteLine($"Now trusting {peer}'s new key{(verified ? " (verified)" : "")}. Run sync to read held messages.");
            break;
        }

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

static void PrintSafetyNumber(string number)
{
    var g = number.Split(' ');
    for (var row = 0; row < 3; row++) Console.WriteLine("    " + string.Join("  ", g[(row * 4)..(row * 4 + 4)]));
}

static bool Confirm(string question)
{
    Console.Write($"{question} [y/N] ");
    return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
}

static void PrintMessage(StoredMessage msg, Messenger? m = null) => Console.WriteLine(Format(msg, m));

/// <summary>One line per message. With <paramref name="m"/>, group messages are prefixed with the group's name.</summary>
static string Format(StoredMessage msg, Messenger? m)
{
    var where = m is not null && msg.GroupId is { } id ? $"#{m.GetGroup(id)?.Name ?? id[..8]} " : "";
    var flag = msg.Status switch { MessageStatus.Pending => " (pending)", MessageStatus.Failed => " (failed)", _ => "" };
    if (msg.IsEvent) return $"[{msg.SentAt.ToLocalTime():HH:mm}] {where}* {msg.Text}";
    return $"[{msg.SentAt.ToLocalTime():HH:mm}] {where}{(msg.Outgoing ? "me" : msg.Peer)}: {msg.Text}{flag}";
}

static bool IsGroupRef(string s) => s.StartsWith('#');

static Group FindGroup(Messenger m, string reference)
{
    var key = reference.TrimStart('#');
    var groups = m.Groups();
    var matches = groups.Where(g => string.Equals(g.Name, key, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count == 0) matches = groups.Where(g => key.Length >= 4 && g.Id.StartsWith(key, StringComparison.OrdinalIgnoreCase)).ToList();
    return matches.Count switch
    {
        1 => matches[0],
        0 => throw new MessengerException($"No group called {reference}. Run: groups"),
        _ => throw new MessengerException($"Several groups are called {reference}; use the start of its id instead (see: groups)."),
    };
}

static IEnumerable<string> SplitUsers(string arg) => arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static async Task Chat(Messenger m, string peer)
{
    var consoleLock = new object();
    void Print(string line) { lock (consoleLock) Console.WriteLine(line); }

    var group = IsGroupRef(peer) ? FindGroup(m, peer) : null;
    if (group is not null)
    {
        foreach (var msg in m.GroupHistory(group.Id, 20)) PrintMessage(msg);
        Print($"--- #{group.Name} (you, {string.Join(", ", group.Members)}) as {m.Username}. Type /quit to leave. ---");
    }
    else
    {
        if (m.GetContact(peer) is null) await m.AddContactAsync(peer);
        foreach (var msg in m.History(peer, 20)) PrintMessage(msg);
        Print($"--- chatting with {peer} as {m.Username}. Type /quit to leave. ---");
    }
    bool InThisChat(StoredMessage msg) => group is null ? msg.GroupId is null && msg.Peer == peer : msg.GroupId == group.Id;

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
                    Print(InThisChat(msg) ? Format(msg, null)
                        : msg.GroupId is { } gid ? $"(new message in #{m.GetGroup(gid)?.Name}; run: chat #{m.GetGroup(gid)?.Name})"
                        : $"(new message from {msg.Peer}; run: chat {msg.Peer})");
                foreach (var p in result.Problems) Print($"! {p}");
                foreach (var u in result.IdentityChanges) Print($"! run 'trust {u}' after checking with them");
            }
            catch (RelayException) when (!cts.IsCancellationRequested)
            {
                if (!relayDown) Print("--- relay unreachable, retrying ---");
                relayDown = true;
            }
            catch (OperationCanceledException) { break; }
            // Push: the relay answers this the moment mail arrives (or after 20 s).
            try { await m.WaitForMessagesAsync(TimeSpan.FromSeconds(20), cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (RelayException)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    });

    while (!cts.IsCancellationRequested)
    {
        var line = await Task.Run(Console.ReadLine);
        if (line is null || line.Trim() == "/quit") break;
        if (string.IsNullOrWhiteSpace(line)) continue;
        try
        {
            var sent = group is null ? await m.SendAsync(peer, line) : await m.SendGroupAsync(group.Id, line);
            if (sent.Status == MessageStatus.Pending) Print("(queued: relay unreachable)");
        }
        catch (MessengerException e) { Print($"! {e.Message}"); }
        catch (RelayException e) { Print($"! {e.Message}"); }
    }
    cts.Cancel();
    await poller;
}
