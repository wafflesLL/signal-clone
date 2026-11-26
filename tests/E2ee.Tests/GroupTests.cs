// Group chats: client-side fan-out over the pairwise sessions, through an in-process relay.

using E2ee.Messaging;
using Microsoft.Data.Sqlite;

namespace E2ee.Tests;

public sealed class GroupTests : IDisposable
{
    private readonly RelayFixture _relay = new();

    public void Dispose() => _relay.Dispose();

    /// <summary>Syncs and returns what arrived as "sender: text" (events as "* text").</summary>
    private static async Task<List<string>> Receive(Messenger m)
    {
        var result = await m.SyncAsync();
        Assert.Empty(result.Problems);
        return result.Received.Select(Line).ToList();
    }

    private static string Line(StoredMessage r) => r.IsEvent ? $"* {r.Text}" : $"{r.Peer}: {r.Text}";

    private async Task<(Messenger Alice, Messenger Bob, Messenger Carol, Group Group)> Study()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        var carol = await _relay.Register("carol");
        var group = await alice.CreateGroupAsync("Study", ["bob", "Carol "]);
        Assert.Equal(["bob", "carol"], group.Members);
        Assert.Equal(["* alice added you to “Study”."], await Receive(bob));
        Assert.Equal(["* alice added you to “Study”."], await Receive(carol));
        return (alice, bob, carol, group);
    }

    [Fact]
    public async Task EveryoneReceivesEveryonesMessages()
    {
        var (alice, bob, carol, group) = await Study();
        Assert.Equal(["alice", "carol"], bob.GetGroup(group.Id)!.Members);
        Assert.Equal("Study", carol.GetGroup(group.Id)!.Name);

        var sent = await alice.SendGroupAsync(group.Id, "meeting at 5?");
        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Equal(["alice: meeting at 5?"], await Receive(bob));
        Assert.Equal(["alice: meeting at 5?"], await Receive(carol));

        // Bob and carol never talked before; the group message starts their session.
        await bob.SendGroupAsync(group.Id, "works for me");
        Assert.Equal(["bob: works for me"], await Receive(alice));
        Assert.Equal(["bob: works for me"], await Receive(carol));

        Assert.Equal(["* You created the group with bob, carol.", "alice: meeting at 5?", "bob: works for me"],
            alice.GroupHistory(group.Id).Select(m => m.Outgoing && !m.IsEvent ? $"alice: {m.Text}" : Line(m)));
        Assert.Empty(alice.History("bob")); // group messages stay out of the direct chat
        Assert.All(carol.GroupHistory(group.Id), m => Assert.Equal(group.Id, m.GroupId));
    }

    [Fact]
    public async Task ConversationListIncludesGroups()
    {
        var (alice, _, _, group) = await Study();
        await alice.SendGroupAsync(group.Id, "latest");
        var first = (await alice.ConversationsAsync())[0];
        Assert.Equal(group.Id, first.Group?.Id);
        Assert.Equal("latest", first.Last?.Text);
    }

    [Fact]
    public async Task AddedMembersGetTheWholeGroup()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        var carol = await _relay.Register("carol");
        var group = await alice.CreateGroupAsync("Study", ["bob"]);
        await Receive(bob);

        await alice.AddGroupMembersAsync(group.Id, ["carol"]);
        Assert.Equal(["* alice added carol."], await Receive(bob));
        Assert.Equal(["* alice added you to “Study”."], await Receive(carol));
        Assert.Equal(["alice", "bob"], carol.GetGroup(group.Id)!.Members);

        await carol.SendGroupAsync(group.Id, "thanks for the invite");
        Assert.Equal(["carol: thanks for the invite"], await Receive(bob)); // bob accepts carol now
        Assert.Equal(["carol: thanks for the invite"], await Receive(alice));

        await Assert.ThrowsAsync<MessengerException>(() => alice.AddGroupMembersAsync(group.Id, ["bob"]));
        await Assert.ThrowsAsync<NotFriendsException>(() => alice.AddGroupMembersAsync(group.Id, ["nobody"]));
    }

    [Fact]
    public async Task LeavingStopsDelivery()
    {
        var (alice, bob, carol, group) = await Study();
        await carol.LeaveGroupAsync(group.Id);
        Assert.Equal(["* carol left."], await Receive(alice));
        Assert.Equal(["* carol left."], await Receive(bob));
        Assert.Equal(["bob"], alice.GetGroup(group.Id)!.Members);

        await alice.SendGroupAsync(group.Id, "just us now");
        Assert.Equal(["alice: just us now"], await Receive(bob));
        Assert.Empty(await Receive(carol)); // nothing was even sent to her
        Assert.False(carol.GetGroup(group.Id)!.Active);
        await Assert.ThrowsAsync<MessengerException>(() => carol.SendGroupAsync(group.Id, "can I still post?"));

        // Being added back works.
        await alice.AddGroupMembersAsync(group.Id, ["carol"]);
        Assert.Equal(["* alice added you to “Study”."], await Receive(carol));
        Assert.True(carol.GetGroup(group.Id)!.Active);
    }

    [Fact]
    public async Task NonMembersCannotPostOrAddThemselves()
    {
        var (alice, bob, _, group) = await Study();
        var mallory = await _relay.Register("mallory");

        // Mallory learned the group id and runs a modified client.
        await mallory.SendRawForTestAsync("bob", new MessageContent("text", "click this link", 1, new GroupContext(group.Id)));
        await mallory.SendRawForTestAsync("bob", new MessageContent("group_update", "", 1,
            new GroupContext(group.Id, "Study", ["alice", "bob", "carol", "mallory"])));

        var result = await bob.SyncAsync();
        Assert.Empty(result.Received);
        Assert.Equal(2, result.Problems.Count);
        Assert.All(result.Problems, p => Assert.Contains("mallory", p));
        Assert.Equal(["alice", "carol"], bob.GetGroup(group.Id)!.Members);
        Assert.Equal(["* alice added you to “Study”."], bob.GroupHistory(group.Id).Select(Line));

        // A member can't kick someone out by sending a shorter member list: updates only add.
        await bob.SendRawForTestAsync("alice", new MessageContent("group_update", "", 1, new GroupContext(group.Id, "Study", ["alice", "bob"])));
        await alice.SyncAsync();
        Assert.Equal(["bob", "carol"], alice.GetGroup(group.Id)!.Members);
    }

    [Fact]
    public async Task UndeliveredCopiesAreRetried()
    {
        var (alice, bob, carol, group) = await Study();
        // Alice sends while the relay is unreachable, then reopens the profile once it's back.
        _relay.Close(alice);
        var offline = Messenger.Open(new MessengerOptions
        {
            DataDirectory = _relay.ProfileDir("alice"),
            HttpClient = new HttpClient(new UnreachableHandler()) { BaseAddress = new Uri("http://localhost/") },
            KeyStore = new InMemoryMasterKeyStore(_relay.ProfileDir("alice")),
        });
        var pending = await offline.SendGroupAsync(group.Id, "sent while offline");
        Assert.Equal(MessageStatus.Pending, pending.Status);
        offline.Dispose();

        var online = _relay.Open("alice");
        await online.SyncAsync();
        Assert.Equal(MessageStatus.Sent, online.GroupHistory(group.Id).Last().Status);
        Assert.Equal(["alice: sent while offline"], await Receive(bob));
        Assert.Equal(["alice: sent while offline"], await Receive(carol));
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("relay down");
    }

    [Fact]
    public async Task ChangedMemberKeyBlocksGroupSending()
    {
        var (alice, bob, _, group) = await Study();
        await alice.SendGroupAsync(group.Id, "hi all");
        await bob.SyncAsync();

        // Bob reinstalls; his new identity shows up when he posts in the group.
        await using (var c = new SqliteConnection($"Data Source={_relay.RelayDbPath};Pooling=False"))
        {
            await c.OpenAsync();
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                DELETE FROM accounts WHERE username = 'bob';
                DELETE FROM signed_prekeys WHERE username = 'bob';
                DELETE FROM one_time_prekeys WHERE username = 'bob';
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var newBob = _relay.Open("bob-reinstalled");
        await newBob.RegisterAsync("bob");
        await newBob.MakeFriendForTestAsync("alice"); // the reinstalled bob still thinks of alice as a friend
        await newBob.SendAsync("alice", "new phone");

        var result = await alice.SyncAsync();
        Assert.Equal(["bob"], result.IdentityChanges);
        var e = await Assert.ThrowsAsync<IdentityChangedException>(() => alice.SendGroupAsync(group.Id, "secret plans"));
        Assert.Equal("bob", e.Username);
        Assert.DoesNotContain(alice.GroupHistory(group.Id), m => m.Text == "secret plans");

        // Leaving still works: it skips bob rather than send to an unchecked key.
        await alice.LeaveGroupAsync(group.Id);
        Assert.False(alice.GetGroup(group.Id)!.Active);
    }

    [Fact]
    public async Task GroupsSurviveRestarts()
    {
        var (alice, bob, _, group) = await Study();
        _relay.Close(bob);
        var bob2 = _relay.Open("bob");
        Assert.Equal("Study", bob2.GetGroup(group.Id)!.Name);
        await alice.SendGroupAsync(group.Id, "still here?");
        Assert.Equal(["alice: still here?"], await Receive(bob2));
    }

    [Fact]
    public async Task RejectsBadGroups()
    {
        var alice = await _relay.Register("alice");
        await _relay.Register("bob");
        await Assert.ThrowsAsync<MessengerException>(() => alice.CreateGroupAsync("  ", ["bob"]));
        await Assert.ThrowsAsync<MessengerException>(() => alice.CreateGroupAsync("Solo", ["alice"]));
        await Assert.ThrowsAsync<NotFriendsException>(() => alice.CreateGroupAsync("Ghosts", ["bob", "ghost"]));
        Assert.Empty(alice.Groups()); // nothing half-created
    }
}
