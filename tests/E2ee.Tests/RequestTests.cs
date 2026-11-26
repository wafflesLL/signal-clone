// Contact requests: nobody can message you, or put you in a group, until you accept them.
// The rules live on each device; the relay only ever sees ordinary encrypted messages.

using E2ee.Messaging;

namespace E2ee.Tests;

public sealed class RequestTests : IDisposable
{
    private readonly RelayFixture _relay = new();

    public void Dispose() => _relay.Dispose();

    [Fact]
    public async Task RequestThenAcceptThenChat()
    {
        var alice = await _relay.Register("alice", friends: false);
        var bob = await _relay.Register("bob", friends: false);

        Assert.Equal(ContactState.RequestSent, (await alice.AddContactAsync("bob")).State);
        await Assert.ThrowsAsync<NotFriendsException>(() => alice.SendAsync("bob", "too soon"));

        var sync = await bob.SyncAsync();
        Assert.Equal(["alice wants to chat with you."], sync.Received.Select(m => m.Text));
        Assert.Equal(ContactState.RequestReceived, bob.GetContact("alice")!.State);
        Assert.Single(await bob.ConversationsAsync());
        await Assert.ThrowsAsync<NotFriendsException>(() => bob.SendAsync("alice", "also too soon"));

        await bob.AcceptRequestAsync("alice");
        Assert.Equal(["bob accepted your request."], (await alice.SyncAsync()).Received.Select(m => m.Text));
        Assert.True(alice.GetContact("bob")!.IsFriend);

        await alice.SendAsync("bob", "hi bob");
        Assert.Equal(["hi bob"], (await bob.SyncAsync()).Received.Select(m => m.Text));
        await bob.SendAsync("alice", "hi alice");
        Assert.Equal(["hi alice"], (await alice.SyncAsync()).Received.Select(m => m.Text));
        Assert.Equal(["bob"], (await alice.ConversationsAsync()).Select(c => c.Contact!.Username));
    }

    [Fact]
    public async Task MessagesFromStrangersAreDropped()
    {
        var alice = await _relay.Register("alice", friends: false);
        var mallory = await _relay.Register("mallory", friends: false);
        // A modified client skips the request and just sends.
        await mallory.MakeFriendForTestAsync("alice");
        await mallory.SendAsync("alice", "buy my stuff");

        var sync = await alice.SyncAsync();
        Assert.Empty(sync.Received);
        Assert.Empty(sync.Problems);
        Assert.Empty(await alice.ConversationsAsync());
        Assert.Empty(await alice.HistoryAsync("mallory"));
        Assert.Empty((await alice.SyncAsync()).Received); // acknowledged, not redelivered
    }

    [Fact]
    public async Task OnlyFriendsCanAddYouToAGroup()
    {
        var alice = await _relay.Register("alice", friends: false);
        var bob = await _relay.Register("bob", friends: false);
        var carol = await _relay.Register("carol", friends: false);
        await RelayFixture.Befriend(alice, bob);
        await RelayFixture.Befriend(alice, carol);

        // Alice can only add her friends, but they needn't be friends with each other.
        await Assert.ThrowsAsync<NotFriendsException>(() => bob.CreateGroupAsync("Nope", ["carol"]));
        var group = await alice.CreateGroupAsync("Study", ["bob", "carol"]);
        await bob.SyncAsync();
        await carol.SyncAsync();
        await bob.SendGroupAsync(group.Id, "hi carol, we haven't met");
        Assert.Contains((await carol.SyncAsync()).Received, m => m.Text == "hi carol, we haven't met");
        // ...and sharing a group doesn't make them friends.
        await Assert.ThrowsAsync<NotFriendsException>(() => carol.SendAsync("bob", "dm?"));

        // A stranger's invite is ignored.
        var mallory = await _relay.Register("mallory", friends: false);
        await mallory.MakeFriendForTestAsync("bob");
        await bob.MakeFriendForTestAsync("mallory");
        var spam = await mallory.CreateGroupAsync("Free stuff", ["bob"]);
        await mallory.MakeFriendForTestAsync("carol"); // mallory's side only: carol never accepted
        await mallory.AddGroupMembersAsync(spam.Id, ["carol"]);
        await carol.SyncAsync();
        Assert.Null(carol.GetGroup(spam.Id));
        Assert.DoesNotContain(await carol.ConversationsAsync(), c => c.Group?.Id == spam.Id);
    }

    [Fact]
    public async Task AskingEachOtherMakesYouFriends()
    {
        var alice = await _relay.Register("alice", friends: false);
        var bob = await _relay.Register("bob", friends: false);
        await alice.AddContactAsync("bob");
        await bob.AddContactAsync("alice"); // before seeing alice's request
        await alice.SyncAsync();
        await bob.SyncAsync();
        Assert.True(alice.GetContact("bob")!.IsFriend);
        Assert.True(bob.GetContact("alice")!.IsFriend);
    }

    [Fact]
    public async Task AddingSomeoneWhoAskedAcceptsThem()
    {
        var alice = await _relay.Register("alice", friends: false);
        var bob = await _relay.Register("bob", friends: false);
        await alice.AddContactAsync("bob");
        await bob.SyncAsync();
        Assert.True((await bob.AddContactAsync("alice")).IsFriend);
        await alice.SyncAsync();
        Assert.True(alice.GetContact("bob")!.IsFriend);
    }

    [Fact]
    public async Task DeclineBlocksQuietly()
    {
        var alice = await _relay.Register("alice", friends: false);
        var mallory = await _relay.Register("mallory", friends: false);
        await mallory.AddContactAsync("alice");
        await alice.SyncAsync();
        await alice.DeclineRequestAsync("mallory");
        Assert.Empty(await alice.ConversationsAsync());

        await mallory.SyncAsync();
        Assert.Equal(ContactState.RequestSent, mallory.GetContact("alice")!.State); // never told

        // Asking again changes nothing.
        await mallory.AddContactAsync("alice");
        Assert.Empty((await alice.SyncAsync()).Received);
        Assert.Equal(ContactState.Blocked, alice.GetContact("mallory")!.State);

        // Alice can still change her mind by adding mallory herself.
        await alice.AddContactAsync("mallory");
        await mallory.SyncAsync();
        Assert.True(mallory.GetContact("alice")!.IsFriend);
        await alice.SyncAsync();
        Assert.True(alice.GetContact("mallory")!.IsFriend);
    }

    [Fact]
    public async Task MembersCanOnlyAddTheirOwnFriends()
    {
        var alice = await _relay.Register("alice", friends: false);
        var bob = await _relay.Register("bob", friends: false);
        var carol = await _relay.Register("carol", friends: false);
        var dave = await _relay.Register("dave", friends: false);
        await RelayFixture.Befriend(alice, bob);
        await RelayFixture.Befriend(alice, dave); // dave is alice's friend, not bob's
        await RelayFixture.Befriend(bob, carol);  // carol is bob's friend, not alice's
        var group = await alice.CreateGroupAsync("Study", ["bob"]);
        await bob.SyncAsync();

        // Bob can't add alice's friend dave...
        var e = await Assert.ThrowsAsync<NotFriendsException>(() => bob.AddGroupMembersAsync(group.Id, ["dave"]));
        Assert.Equal("dave", e.Username);
        // ...or a total stranger, or anyone who only sent him a request.
        await Assert.ThrowsAsync<NotFriendsException>(() => bob.AddGroupMembersAsync(group.Id, ["nobody"]));
        await dave.AddContactAsync("bob");
        await bob.SyncAsync();
        await Assert.ThrowsAsync<NotFriendsException>(() => bob.AddGroupMembersAsync(group.Id, ["dave"]));
        Assert.Equal(["alice"], bob.GetGroup(group.Id)!.Members);

        // But he can add his own friend carol, who joins because bob is her friend.
        await bob.AddGroupMembersAsync(group.Id, ["carol"]);
        await carol.SyncAsync();
        await alice.SyncAsync();
        Assert.Equal(["alice", "bob"], carol.GetGroup(group.Id)!.Members);
        Assert.Equal(["bob", "carol"], alice.GetGroup(group.Id)!.Members);
        await alice.SendGroupAsync(group.Id, "welcome carol");
        Assert.Contains((await carol.SyncAsync()).Received, m => m.Text == "welcome carol");
    }

    [Fact]
    public async Task AModifiedClientCannotPullAStrangerIntoAGroup()
    {
        var alice = await _relay.Register("alice", friends: false);
        var mallory = await _relay.Register("mallory", friends: false);
        var victim = await _relay.Register("victim", friends: false);
        await RelayFixture.Befriend(alice, mallory);
        var group = await alice.CreateGroupAsync("Club", ["mallory"]);
        await mallory.SyncAsync();

        // Mallory's client skips the friend check (marks victim a friend on her side only).
        await mallory.MakeFriendForTestAsync("victim");
        await mallory.AddGroupMembersAsync(group.Id, ["victim"]);
        await mallory.SendGroupAsync(group.Id, "spam for victim");

        // Victim never joins and never sees the group's messages.
        var sync = await victim.SyncAsync();
        Assert.Empty(sync.Received);
        Assert.Null(victim.GetGroup(group.Id));
        Assert.Empty(await victim.ConversationsAsync());
    }
}
