// End-to-end tests: real Messenger profiles talking through an in-process relay.

using System.Text;
using E2ee.Messaging;
using Microsoft.Data.Sqlite;

namespace E2ee.Tests;

public sealed class MessagingTests : IDisposable
{
    private readonly RelayFixture _relay = new();

    public void Dispose() => _relay.Dispose();

    private static async Task<List<string>> Receive(Messenger m)
    {
        var result = await m.SyncAsync();
        Assert.Empty(result.Problems);
        return result.Received.Select(r => $"{r.Peer}: {r.Text}").ToList();
    }

    [Fact]
    public async Task FirstContactAndReply()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");

        var sent = await alice.SendAsync("bob", "hello bob");
        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Equal(["alice: hello bob"], await Receive(bob));

        await bob.SendAsync("alice", "hi alice");
        Assert.Equal(["bob: hi alice"], await Receive(alice));

        Assert.Equal(["alice"], bob.Contacts().Select(c => c.Username));
        Assert.Equal(new[] { "hello bob", "hi alice" }, alice.History("bob").Select(h => h.Text));
        Assert.Empty(await Receive(bob)); // inbox was acknowledged and emptied
    }

    [Fact]
    public async Task SeveralMessagesBeforeAnyReply()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        for (var i = 0; i < 5; i++) await alice.SendAsync("bob", $"m{i}");
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"alice: m{i}"), await Receive(bob));

        await bob.SendAsync("alice", "got them");
        Assert.Equal(["bob: got them"], await Receive(alice));
        await alice.SendAsync("bob", "great");
        Assert.Equal(["alice: great"], await Receive(bob));
    }

    [Fact]
    public async Task ConversationSurvivesRestarts()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        await alice.SendAsync("bob", "before restart");
        Assert.Equal(["alice: before restart"], await Receive(bob));

        _relay.Close(alice);
        _relay.Close(bob);
        alice = _relay.Open("alice");
        bob = _relay.Open("bob");
        Assert.True(alice.IsRegistered);
        Assert.Equal("alice", alice.Username);

        for (var i = 0; i < 3; i++)
        {
            await bob.SendAsync("alice", $"b{i}");
            Assert.Equal([$"bob: b{i}"], await Receive(alice));
            await alice.SendAsync("bob", $"a{i}");
            Assert.Equal([$"alice: a{i}"], await Receive(bob));
        }
        Assert.Equal(7, alice.History("bob").Count); // 1 before the restart + 3 round trips
    }

    [Fact]
    public async Task SimultaneousFirstContactConverges()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        await alice.SendAsync("bob", "hi from alice");
        await bob.SendAsync("alice", "hi from bob");
        Assert.Equal(["bob: hi from bob"], await Receive(alice));
        Assert.Equal(["alice: hi from alice"], await Receive(bob));

        for (var i = 0; i < 4; i++)
        {
            await alice.SendAsync("bob", $"a{i}");
            await bob.SendAsync("alice", $"b{i}");
            Assert.Equal([$"alice: a{i}"], await Receive(bob));
            Assert.Equal([$"bob: b{i}"], await Receive(alice));
        }
    }

    [Fact]
    public async Task WorksWithoutOneTimePrekeysAndRefills()
    {
        var policy = new PrekeyPolicy { InitialOneTimePrekeys = 1, RefillBelow = 2, RefillBatch = 10 };
        var bob = await _relay.Register("bob", policy);
        var alice = await _relay.Register("alice");
        var carol = await _relay.Register("carol");

        await alice.SendAsync("bob", "uses the only OPK");
        await carol.SendAsync("bob", "gets no OPK");
        Assert.Equal(["alice: uses the only OPK", "carol: gets no OPK"], await Receive(bob));

        var status = await StatusOf(bob);
        Assert.Equal(10, status);
    }

    private async Task<int> StatusOf(Messenger m)
    {
        await using var c = new SqliteConnection($"Data Source={_relay.RelayDbPath};Pooling=False");
        await c.OpenAsync();
        var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM one_time_prekeys WHERE username = $u";
        cmd.Parameters.AddWithValue("$u", m.Username);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task UsernameBelongsToFirstKey()
    {
        await _relay.Register("alice");
        var impostor = _relay.Open("impostor");
        await Assert.ThrowsAsync<UsernameTakenException>(() => impostor.RegisterAsync("alice"));
    }

    [Fact]
    public async Task UnknownRecipientIsReported()
    {
        var alice = await _relay.Register("alice");
        await Assert.ThrowsAsync<NotFriendsException>(() => alice.SendAsync("nobody", "hello?"));
        await Assert.ThrowsAsync<UserNotFoundException>(() => alice.AddContactAsync("nobody"));
    }

    [Fact]
    public async Task RelaySwappingIdentityKeyIsDetected()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        await alice.AddContactAsync("bob"); // alice remembers bob's real key

        // A malicious relay replaces bob's keys with ones it controls.
        var mallory = X3dh.GenerateIdentity();
        var (_, bundles) = X3dh.GeneratePrekeys(mallory, 1);
        var b = bundles[0];
        await using (var c = new SqliteConnection($"Data Source={_relay.RelayDbPath};Pooling=False"))
        {
            await c.OpenAsync();
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE accounts SET sig_pub = $s, dh_pub = $d WHERE username = 'bob';
                UPDATE signed_prekeys SET id = 1, pub = $p, sig = $g WHERE username = 'bob';
                DELETE FROM one_time_prekeys WHERE username = 'bob';
                """;
            cmd.Parameters.AddWithValue("$s", b.IdentitySigPub);
            cmd.Parameters.AddWithValue("$d", b.IdentityDhPub);
            cmd.Parameters.AddWithValue("$p", b.SignedPrekeyPub);
            cmd.Parameters.AddWithValue("$g", b.SignedPrekeySig);
            await cmd.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<IdentityChangedException>(() => alice.SendAsync("bob", "secret"));
        Assert.Empty(alice.History("bob"));
    }

    [Fact]
    public async Task RelayReplayingAnEnvelopeDoesNotDuplicateIt()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        await alice.SendAsync("bob", "once");

        // The relay re-queues a copy of the envelope under a new id.
        await using (var c = new SqliteConnection($"Data Source={_relay.RelayDbPath};Pooling=False"))
        {
            await c.OpenAsync();
            var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO inbox (recipient, sender, received_at, envelope) SELECT recipient, sender, received_at, envelope FROM inbox";
            await cmd.ExecuteNonQueryAsync();
        }

        var result = await bob.SyncAsync();
        Assert.Equal(["once"], result.Received.Select(r => r.Text));
        Assert.Single(result.Problems);
        Assert.Single(bob.History("alice"));
    }

    [Fact]
    public async Task NoPlaintextAtRestOrOnTheRelay()
    {
        const string secret = "the eagle lands at midnight";
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        await alice.SendAsync("bob", secret);
        Assert.Equal(["alice: " + secret], await Receive(bob));
        await bob.SendAsync("alice", secret + " (reply)");
        Assert.Single(await Receive(alice));

        _relay.Close(alice);
        _relay.Close(bob);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_relay.RelayDbPath)!, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.DoesNotContain("eagle", Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain("eagle", Encoding.Unicode.GetString(bytes));
        }
    }

    [Fact]
    public async Task ProfileCanOnlyBeOpenedOnce()
    {
        await _relay.Register("alice");
        Assert.Throws<ProfileInUseException>(() => _relay.Open("alice"));
    }

    /// <summary>Simulates bob losing his device: the relay forgets him and a new install claims the name.</summary>
    private async Task<Messenger> ReinstallBob()
    {
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
        return newBob;
    }

    [Fact]
    public async Task BothSidesComputeTheSameSafetyNumber()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        await alice.SendAsync("bob", "hi");
        Assert.Single(await Receive(bob));
        Assert.Equal(await alice.SafetyNumberAsync("bob"), await bob.SafetyNumberAsync("alice"));

        Assert.False(alice.GetContact("bob")!.Verified);
        await alice.SetVerifiedAsync("bob", true);
        Assert.True(alice.GetContact("bob")!.Verified);
    }

    [Fact]
    public async Task KeyChangeHoldsMessagesUntilTrusted()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        await alice.SendAsync("bob", "hi bob");
        Assert.Single(await Receive(bob));
        await alice.SetVerifiedAsync("bob", true);

        var newBob = await ReinstallBob();
        await newBob.SendAsync("alice", "it's me, new phone");

        // Alice holds the message, flags bob, and clears the verified mark.
        var sync = await alice.SyncAsync();
        Assert.Empty(sync.Received);
        Assert.Equal(["bob"], sync.IdentityChanges);
        var flagged = alice.GetContact("bob")!;
        Assert.True(flagged.IdentityChanged);
        Assert.False(flagged.Verified);
        Assert.Single(alice.History("bob")); // only "hi bob"
        await Assert.ThrowsAsync<IdentityChangedException>(() => alice.SendAsync("bob", "are you there?"));

        // A second sync doesn't report it again, and the message is still waiting on the relay.
        var again = await alice.SyncAsync();
        Assert.Empty(again.IdentityChanges);
        Assert.Empty(again.Received);

        // Alice reviews the new key: its safety number is the one new-bob sees.
        var check = await alice.CheckIdentityAsync("bob");
        Assert.False(check.MatchesTrustedKey);
        Assert.Equal(await newBob.SafetyNumberAsync("alice"), check.SafetyNumber);

        await alice.TrustIdentityAsync(check, verified: true);
        Assert.Equal(["bob: it's me, new phone"], await Receive(alice));
        Assert.True(alice.GetContact("bob")!.Verified);

        await alice.SendAsync("bob", "welcome back");
        Assert.Equal(["alice: welcome back"], await Receive(newBob));
    }

    [Fact]
    public async Task TrustUsesTheReviewedKeyNotTheRelaysLatest()
    {
        var alice = await _relay.Register("alice");
        await _relay.Register("bob");
        await alice.AddContactAsync("bob");
        var newBob = await ReinstallBob();
        await newBob.SendAsync("alice", "hello");
        await alice.SyncAsync();
        var check = await alice.CheckIdentityAsync("bob");

        // Between review and trust, the relay swaps bob's key again.
        var (_, bundles) = X3dh.GeneratePrekeys(X3dh.GenerateIdentity(), 1);
        await using (var c = new SqliteConnection($"Data Source={_relay.RelayDbPath};Pooling=False"))
        {
            await c.OpenAsync();
            var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET sig_pub = $s, dh_pub = $d WHERE username = 'bob'";
            cmd.Parameters.AddWithValue("$s", bundles[0].IdentitySigPub);
            cmd.Parameters.AddWithValue("$d", bundles[0].IdentityDhPub);
            await cmd.ExecuteNonQueryAsync();
        }

        var trusted = await alice.TrustIdentityAsync(check);
        Assert.Equal(check.IdentitySigPub, trusted.IdentitySigPub); // the key alice saw, not the swapped one
        Assert.Equal(["bob: hello"], await Receive(alice));
    }

    [Fact]
    public async Task WaitReturnsAsSoonAsMailArrives()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");

        var started = DateTimeOffset.UtcNow;
        var wait = bob.WaitForMessagesAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(300);
        Assert.False(wait.IsCompleted);
        await alice.SendAsync("bob", "ping");
        Assert.True(await wait);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal(["alice: ping"], await Receive(bob));
    }

    [Fact]
    public async Task WaitTimesOutWhenThereIsNoNewMail()
    {
        var bob = await _relay.Register("bob");
        Assert.False(await bob.WaitForMessagesAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task WaitIgnoresMessagesOnHold()
    {
        var alice = await _relay.Register("alice");
        await _relay.Register("bob");
        await alice.AddContactAsync("bob");
        var newBob = await ReinstallBob();
        await newBob.SendAsync("alice", "held");
        await alice.SyncAsync(); // held: stays on the relay

        // Without the "after" marker this would return immediately forever.
        Assert.False(await alice.WaitForMessagesAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task SendingDoesNotWaitForAPendingPush()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        var wait = alice.WaitForMessagesAsync(TimeSpan.FromSeconds(20));
        var send = alice.SendAsync("bob", "not blocked by the open wait");
        Assert.Same(send, await Task.WhenAny(send, Task.Delay(5000)));
        Assert.Single(await Receive(bob));
        await bob.SendAsync("alice", "done");
        Assert.True(await wait);
    }
}
