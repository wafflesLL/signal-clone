// Images, videos and reactions, end to end through an in-process relay.

using E2ee.Messaging;
using E2ee.Server;
using E2ee.Wire;
using Microsoft.Extensions.DependencyInjection;

namespace E2ee.Tests;

public sealed class MediaTests : IDisposable
{
    private readonly RelayFixture _relay = new();
    private readonly string _files = Path.Combine(Path.GetTempPath(), $"e2ee-media-{Guid.NewGuid():N}");

    public MediaTests() => Directory.CreateDirectory(_files);

    public void Dispose()
    {
        _relay.Dispose();
        try { Directory.Delete(_files, recursive: true); } catch (IOException) { }
    }

    private RelayStore RelayStore => _relay.Factory.Services.GetRequiredService<RelayStore>();

    private string BlobsOnRelay() => string.Join(",", Directory.GetFiles(RelayStore.AttachmentDir).Select(Path.GetFileName));

    private string MakeFile(string name, int size)
    {
        var path = Path.Combine(_files, name);
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<SyncResult> SyncClean(Messenger m)
    {
        var result = await m.SyncAsync();
        Assert.Empty(result.Problems);
        return result;
    }

    [Fact]
    public async Task ImageArrivesAndOnlyDevicesKeepIt()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        var photo = MakeFile("beach.jpg", 300_000);

        var sent = await alice.SendFileAsync("bob", photo, "look at this");
        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Equal(new Attachment("beach.jpg", "image/jpeg", 300_000, AttachmentState.Ready), sent.Attachment);
        Assert.Equal(File.ReadAllBytes(photo), await alice.AttachmentBytesAsync(sent.Id)); // the sender keeps a copy
        Assert.NotEqual("", BlobsOnRelay());

        var got = Assert.Single((await SyncClean(bob)).Received);
        Assert.Equal("look at this", got.Text);
        Assert.Equal(AttachmentState.Ready, got.Attachment!.State);
        Assert.Equal(File.ReadAllBytes(photo), await bob.AttachmentBytesAsync(got.Id));

        // The relay held only ciphertext, and deleted it once bob had it.
        Assert.Equal("", BlobsOnRelay());
        // On bob's device it is encrypted at rest: the plaintext appears nowhere in his profile.
        var plain = File.ReadAllBytes(photo)[..64];
        _relay.Close(bob);
        foreach (var f in Directory.GetFiles(_relay.ProfileDir("bob"), "*", SearchOption.AllDirectories))
            Assert.False(File.ReadAllBytes(f).AsSpan().IndexOf(plain) >= 0, $"plaintext found in {f}");
    }

    [Fact]
    public async Task GroupVideoStaysUntilEveryoneHasIt()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        var carol = await _relay.Register("carol");
        var group = await alice.CreateGroupAsync("Trip", ["bob", "carol"]);
        await bob.SyncAsync();
        await carol.SyncAsync();

        var clip = MakeFile("waves.mov", 2_000_000);
        await alice.SendGroupFileAsync(group.Id, clip);
        var atBob = Assert.Single((await SyncClean(bob)).Received);
        Assert.Equal("video/quicktime", atBob.Attachment!.ContentType);
        Assert.True(atBob.Attachment.IsVideo);
        Assert.Equal(group.Id, atBob.GroupId);
        Assert.NotEqual("", BlobsOnRelay()); // carol hasn't fetched it yet

        var atCarol = Assert.Single((await SyncClean(carol)).Received);
        Assert.Equal(File.ReadAllBytes(clip), await carol.AttachmentBytesAsync(atCarol.Id));
        Assert.Equal("", BlobsOnRelay());
    }

    [Fact]
    public async Task OnlyImagesAndVideosWithinTheLimit()
    {
        var alice = await _relay.Register("alice");
        await _relay.Register("bob");
        await Assert.ThrowsAsync<MessengerException>(() => alice.SendFileAsync("bob", MakeFile("run.sh", 10)));
        await Assert.ThrowsAsync<MessengerException>(() => alice.SendFileAsync("bob", MakeFile("empty.png", 0)));
        var huge = Path.Combine(_files, "huge.mp4");
        using (var f = File.Create(huge)) f.SetLength(Attachments.MaxPlaintextBytes + 1);
        var e = await Assert.ThrowsAsync<MessengerException>(() => alice.SendFileAsync("bob", huge));
        Assert.Contains("limit", e.Message);
        Assert.Empty(alice.History("bob"));
    }

    [Fact]
    public async Task TamperedBlobIsDiscarded()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        await alice.SendFileAsync("bob", MakeFile("cat.png", 5000));

        // The relay flips a byte in the stored ciphertext.
        var blob = Directory.GetFiles(RelayStore.AttachmentDir).Single();
        var bytes = File.ReadAllBytes(blob);
        bytes[100] ^= 1;
        File.WriteAllBytes(blob, bytes);

        var result = await bob.SyncAsync();
        Assert.Contains(result.Problems, p => p.Contains("didn't match"));
        var msg = Assert.Single(result.Received);
        Assert.Equal(AttachmentState.Failed, msg.Attachment!.State);
        await Assert.ThrowsAsync<MessengerException>(() => bob.AttachmentBytesAsync(msg.Id));
    }

    [Fact]
    public async Task OnlyNamedRecipientsCanFetch()
    {
        var alice = await _relay.Register("alice");
        await _relay.Register("bob");
        var mallory = await _relay.Register("mallory");
        await alice.SendFileAsync("bob", MakeFile("secret.png", 5000));
        var id = Path.GetFileName(Directory.GetFiles(RelayStore.AttachmentDir).Single());

        var e = await Assert.ThrowsAsync<RelayException>(() => mallory.RelayForTest.DownloadAttachmentAsync(id));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, e.Status);
    }

    [Fact]
    public async Task DisguisedProgramIsRefused()
    {
        var mallory = await _relay.Register("mallory");
        var bob = await _relay.Register("bob");
        // A modified client claims a video type for an app bundle.
        var pointer = new AttachmentPointer(new string('a', 32), new byte[32], new byte[32], 10, "video/mp4", "Totally a video.app");
        await mallory.SendRawForTestAsync("bob", new MessageContent("text", "watch this", 1, Attachment: pointer));

        var result = await bob.SyncAsync();
        Assert.Contains(result.Problems, p => p.Contains("isn't an image or video"));
        Assert.Null(Assert.Single(result.Received).Attachment);
    }

    [Fact]
    public async Task ExportUsesASafeNameAndExtension()
    {
        var alice = await _relay.Register("alice");
        await _relay.Register("bob");
        var sent = await alice.SendFileAsync("bob", MakeFile("..hidden.JPG", 1000));
        Assert.Equal("..hidden.JPG", sent.Attachment!.FileName);

        var dir = Path.Combine(_files, "out");
        var first = await alice.ExportAttachmentAsync(sent.Id, dir);
        var second = await alice.ExportAttachmentAsync(sent.Id, dir);
        Assert.Equal("hidden.jpg", Path.GetFileName(first));
        Assert.Equal("hidden (2).jpg", Path.GetFileName(second));

        var opened = await alice.ExportAttachmentAsync(sent.Id, alice.OpenFolder);
        Assert.True(File.Exists(opened));
        _relay.Close(alice);
        Assert.False(Directory.Exists(Path.GetDirectoryName(opened))); // plaintext copies go when the app closes
    }

    [Fact]
    public async Task ReactionsInADirectChat()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        var hello = await alice.SendAsync("bob", "passed the exam!");
        var atBob = Assert.Single((await SyncClean(bob)).Received);

        await bob.ReactAsync(atBob.Id, "🎉");
        var update = Assert.Single((await SyncClean(alice)).Updated);
        Assert.Equal(hello.Id, update.Id);
        Assert.Equal([new Reaction("bob", "🎉")], update.Reactions);
        Assert.Empty((await SyncClean(alice)).Received); // a reaction isn't a message

        await bob.ReactAsync(atBob.Id, "❤️"); // replaces
        await alice.ReactAsync(hello.Id, "👍"); // reacting to your own message is fine
        await SyncClean(alice);
        Assert.Equal([new Reaction("bob", "❤️"), new Reaction("alice", "👍")],
            alice.History("bob").Single().Reactions.OrderBy(r => r.Reactor == "alice").ToList());
        Assert.Equal([new Reaction("alice", "👍"), new Reaction("bob", "❤️")],
            (await SyncClean(bob)).Updated.Single().Reactions.OrderBy(r => r.Reactor).ToList());

        await bob.ReactAsync(atBob.Id, null); // takes it back
        await SyncClean(alice);
        Assert.Equal([new Reaction("alice", "👍")], alice.History("bob").Single().Reactions);
        Assert.Equal(["passed the exam!"], alice.History("bob").Select(m => m.Text)); // no extra rows
        await Assert.ThrowsAsync<MessengerException>(() => alice.ReactAsync(hello.Id, "lol"));
    }

    [Fact]
    public async Task ReactionsInAGroup()
    {
        var alice = await _relay.Register("alice");
        var bob = await _relay.Register("bob");
        var carol = await _relay.Register("carol");
        var mallory = await _relay.Register("mallory");
        var group = await alice.CreateGroupAsync("Study", ["bob", "carol"]);
        var msg = await alice.SendGroupAsync(group.Id, "quiz moved to friday");
        await bob.SyncAsync();
        var atCarol = (await carol.SyncAsync()).Received.Single(m => !m.IsEvent);

        await carol.ReactAsync(atCarol.Id, "😮");
        Assert.Equal([new Reaction("carol", "😮")], Assert.Single((await SyncClean(alice)).Updated).Reactions);
        Assert.Equal([new Reaction("carol", "😮")], Assert.Single((await SyncClean(bob)).Updated).Reactions);

        // Someone outside the group can't react in it.
        await mallory.SendRawForTestAsync("alice", new MessageContent("reaction", "", 1, new GroupContext(group.Id),
            Reaction: new ReactionContent("alice", msg.SentAt.ToUnixTimeMilliseconds(), "💩")));
        await alice.SyncAsync();
        Assert.Equal([new Reaction("carol", "😮")], alice.GroupHistory(group.Id).Single(m => m.Id == msg.Id).Reactions);
    }
}
