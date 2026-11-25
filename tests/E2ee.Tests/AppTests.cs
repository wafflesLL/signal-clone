// Desktop app tests: the real Avalonia windows, rendered headlessly with Skia, driving a
// real Messenger against an in-process relay. Set E2EE_SCREENSHOTS to a folder to save
// PNG screenshots of each screen.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using E2ee.App;
using E2ee.App.ViewModels;
using E2ee.App.Views;
using E2ee.Messaging;
using Microsoft.Data.Sqlite;

[assembly: AvaloniaTestApplication(typeof(E2ee.Tests.TestAppBuilder))]

namespace E2ee.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<E2ee.App.App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class AppTests : IDisposable
{
    private readonly RelayFixture _relay = new();

    public void Dispose() => _relay.Dispose();

    private AppOptions Options(string profile) => new(_relay.ProfileDir(profile), false, "http://localhost/")
    {
        HttpClientFactory = _relay.Factory.CreateClient,
        KeyStore = new InMemoryMasterKeyStore(_relay.ProfileDir(profile)),
    };

    private static Window Show(MainWindowViewModel vm)
    {
        var window = new MainWindow { DataContext = vm, Width = 980, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Find<T>(Window w, string name) where T : Control =>
        w.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Screenshot(Window w, string name)
    {
        var dir = Environment.GetEnvironmentVariable("E2EE_SCREENSHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
    }

    private static void Type(Window w, TextBox box, string text, bool enter = false)
    {
        box.Focus();
        w.KeyTextInput(text);
        if (enter) w.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets queued UI work and background Messenger calls finish.</summary>
    private static async Task Settle()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task NewProfileShowsSignUpAndRegisters()
    {
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var onboarding = Assert.IsType<OnboardingViewModel>(vm.Current);
        Assert.False(onboarding.RegisterCommand.CanExecute(null)); // no username yet

        Type(window, Find<TextBox>(window, "UsernameBox"), "Alice");
        Assert.Equal("Alice", onboarding.Username);
        Screenshot(window, "1-sign-up");

        await onboarding.RegisterCommand.ExecuteAsync(null);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        Assert.Equal("alice", shell.Me);
        Assert.Equal("E2EE Messenger — alice", vm.Title);
        Screenshot(window, "2-empty-shell");
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task SignUpShowsRelayErrors()
    {
        await _relay.Register("alice");
        var vm = new MainWindowViewModel(Options("impostor"), startPolling: false);
        Show(vm);
        var onboarding = Assert.IsType<OnboardingViewModel>(vm.Current);

        onboarding.Username = "bad name!";
        await onboarding.RegisterCommand.ExecuteAsync(null);
        Assert.Contains("lowercase", onboarding.Error);

        onboarding.Username = "alice";
        await onboarding.RegisterCommand.ExecuteAsync(null);
        Assert.Contains("already registered", onboarding.Error);
        Assert.IsType<OnboardingViewModel>(vm.Current);
    }

    [AvaloniaFact]
    public async Task ChatEndToEnd()
    {
        var bob = await _relay.Register("bob");
        var carol = await _relay.Register("carol");
        _relay.Close(await _relay.Register("alice")); // alice signed up earlier; now she opens the app

        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;

        // Add bob and send with the Enter key, as a user would.
        Type(window, Find<TextBox>(window, "NewContactBox"), "bob", enter: true);
        await Settle();
        Assert.Equal("bob", shell.Selected?.Username);

        Type(window, Find<TextBox>(window, "DraftBox"), "Hi Bob! This is end-to-end encrypted.", enter: true);
        await Settle();
        var sent = Assert.Single(shell.Messages);
        Assert.True(sent.Outgoing);
        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Equal("", shell.Draft);

        // Bob reads it and replies; alice's next poll shows the reply in the open chat.
        Assert.Equal(["Hi Bob! This is end-to-end encrypted."], (await bob.SyncAsync()).Received.Select(m => m.Text));
        await bob.SendAsync("alice", "Got it. The relay only saw ciphertext.");
        await bob.SendAsync("alice", "Want to verify safety numbers later?");
        await shell.SyncNowAsync();
        Assert.Equal(3, shell.Messages.Count);
        Assert.False(shell.Messages[1].Outgoing);
        Assert.Equal("Want to verify safety numbers later?", shell.Contacts.Single(c => c.Username == "bob").Preview);
        Assert.Equal(0, shell.Contacts.Single(c => c.Username == "bob").Unread); // the chat is open

        // A message from someone new adds them to the top of the list with an unread badge.
        await carol.SendAsync("alice", "hey alice, it's carol");
        await shell.SyncNowAsync();
        Assert.Equal("carol", shell.Contacts[0].Username);
        Assert.Equal(1, shell.Contacts[0].Unread);
        Assert.Equal("hey alice, it's carol", shell.Contacts[0].Preview);
        Assert.Equal(3, shell.Messages.Count); // still showing bob's chat
        Screenshot(window, "3-chat");

        // Opening carol's chat clears the badge and shows her message.
        shell.Selected = shell.Contacts[0];
        await Settle();
        Assert.Equal(0, shell.Contacts[0].Unread);
        Assert.Equal(["hey alice, it's carol"], shell.Messages.Select(m => m.Text));

        // History survives closing and reopening the app.
        vm.Dispose();
        var reopened = new MainWindowViewModel(Options("alice"), startPolling: false);
        var shell2 = Assert.IsType<ShellViewModel>(reopened.Current);
        await reopened.ShellReady;
        Assert.Equal(["carol", "bob"], shell2.Contacts.Select(c => c.Username));
        Assert.Equal("Want to verify safety numbers later?", shell2.Contacts[1].Preview);
        reopened.Dispose();
    }

    [AvaloniaFact]
    public async Task UnknownContactShowsNotice()
    {
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;

        shell.NewContact = "nobody";
        await shell.AddContactCommand.ExecuteAsync(null);
        Assert.Contains("No user named 'nobody'", shell.Notice);
        Assert.Empty(shell.Contacts);
        shell.DismissNoticeCommand.Execute(null);
        Assert.False(shell.HasNotice);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task ChangedIdentityKeyBlocksSendingAndOffersTrust()
    {
        await _relay.Register("bob");
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        shell.NewContact = "bob";
        await shell.AddContactCommand.ExecuteAsync(null);

        // The relay swaps bob's identity for one it controls.
        var (_, bundles) = X3dh.GeneratePrekeys(X3dh.GenerateIdentity(), 1);
        var b = bundles[0];
        await using (var c = new SqliteConnection($"Data Source={_relay.RelayDbPath};Pooling=False"))
        {
            await c.OpenAsync();
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE accounts SET sig_pub = $s, dh_pub = $d WHERE username = 'bob';
                UPDATE signed_prekeys SET id = 1, pub = $p, sig = $g WHERE username = 'bob';
                """;
            cmd.Parameters.AddWithValue("$s", b.IdentitySigPub);
            cmd.Parameters.AddWithValue("$d", b.IdentityDhPub);
            cmd.Parameters.AddWithValue("$p", b.SignedPrekeyPub);
            cmd.Parameters.AddWithValue("$g", b.SignedPrekeySig);
            await cmd.ExecuteNonQueryAsync();
        }

        shell.Draft = "this should not be sent";
        await shell.SendCommand.ExecuteAsync(null);
        Assert.Empty(shell.Messages);
        Assert.Equal("this should not be sent", shell.Draft); // kept so nothing is lost
        Assert.True(shell.Selected!.IdentityChanged);
        Assert.Contains("identity key does not match", shell.Notice);
        Assert.True(shell.TrustNewKeyCommand.CanExecute(null));
        Screenshot(window, "4-identity-changed");
        vm.Dispose();
    }
}
