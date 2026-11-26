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
        PasswordCost = PasswordCost.ForTests,
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
        Assert.False(onboarding.RegisterCommand.CanExecute(null)); // no password yet
        Type(window, Find<TextBox>(window, "PasswordBox"), "correct horse");
        Type(window, Find<TextBox>(window, "ConfirmPasswordBox"), "correct horse");
        Assert.True(onboarding.RegisterCommand.CanExecute(null));
        Screenshot(window, "1-sign-up");

        await onboarding.RegisterCommand.ExecuteAsync(null);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        Assert.Equal("alice", shell.Me);
        Assert.Equal("E2EE Messenger — alice", vm.Title);
        Assert.True(PasswordMasterKeyStore.IsLocked(_relay.ProfileDir("alice")));
        Screenshot(window, "2-empty-shell");
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task SignUpChecksThePassword()
    {
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        Show(vm);
        var onboarding = Assert.IsType<OnboardingViewModel>(vm.Current);
        onboarding.Username = "alice";

        (onboarding.Password, onboarding.ConfirmPassword) = ("short", "short");
        await onboarding.RegisterCommand.ExecuteAsync(null);
        Assert.Contains("at least 8", onboarding.Error);

        (onboarding.Password, onboarding.ConfirmPassword) = ("correct horse", "correct hose");
        await onboarding.RegisterCommand.ExecuteAsync(null);
        Assert.Contains("don't match", onboarding.Error);
        Assert.False(Directory.Exists(_relay.ProfileDir("alice"))); // nothing written yet
        Assert.IsType<OnboardingViewModel>(vm.Current);
    }

    [AvaloniaFact]
    public async Task PasswordLocksTheApp()
    {
        var bob = await _relay.Register("bob");
        var options = Options("alice");
        var vm = new MainWindowViewModel(options, startPolling: false);
        var signUp = Assert.IsType<OnboardingViewModel>(vm.Current);
        (signUp.Username, signUp.Password, signUp.ConfirmPassword) = ("alice", "correct horse", "correct horse");
        await signUp.RegisterCommand.ExecuteAsync(null);
        await vm.ShellReady;
        var first = Assert.IsType<ShellViewModel>(vm.Current);
        await bob.AddContactAsync("alice");
        await first.SyncNowAsync();
        first.Selected = Assert.Single(first.Contacts);
        await first.AcceptRequestCommand.ExecuteAsync(null);
        await bob.SyncAsync();
        await bob.SendAsync("alice", "only readable after unlocking");
        await first.SyncNowAsync();
        vm.Dispose();

        // The master key is stored only under the password: no other key store has it.
        Assert.Throws<MessengerException>(() => Messenger.Open(options.ToMessengerOptions(null, null) with { KeyStore = new InMemoryMasterKeyStore("nothing-here") }).Dispose());

        // Reopening asks for the password.
        vm = new MainWindowViewModel(options, startPolling: false);
        var window = Show(vm);
        var unlock = Assert.IsType<UnlockViewModel>(vm.Current);
        Type(window, Find<TextBox>(window, "UnlockPasswordBox"), "wrong password", enter: true);
        await Settle();
        Assert.Equal("That password is incorrect.", unlock.Error);
        Assert.Equal("", unlock.Password);
        Screenshot(window, "15-unlock");

        Type(window, Find<TextBox>(window, "UnlockPasswordBox"), "correct horse", enter: true);
        await Settle();
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        Assert.Equal("alice", shell.Me);
        Assert.Equal("only readable after unlocking", shell.Contacts.Single().Preview);
        vm.Dispose();
    }

    private AppOptions AccountOptions(string profile) => new(_relay.ProfileDir(profile), false, "http://localhost/")
    {
        HttpClientFactory = _relay.Factory.CreateClient,
        PasswordCost = PasswordCost.ForTests,
        ProfilesRoot = Path.GetDirectoryName(_relay.ProfileDir(profile)),
    };

    private static async Task SignUp(MainWindowViewModel vm, string name, string password)
    {
        var signUp = Assert.IsType<OnboardingViewModel>(vm.Current);
        (signUp.Username, signUp.Password, signUp.ConfirmPassword) = (name, password, password);
        await signUp.RegisterCommand.ExecuteAsync(null);
        await vm.ShellReady;
        Assert.Equal(name, Assert.IsType<ShellViewModel>(vm.Current).Me);
    }

    [AvaloniaFact]
    public async Task SignInWithAnotherAccount()
    {
        var first = AccountOptions("default");
        var vm = new MainWindowViewModel(first, startPolling: false);
        // With no other accounts yet, sign-up doesn't offer to switch.
        Assert.False(Assert.IsType<OnboardingViewModel>(vm.Current).CanUseOtherAccount);
        await SignUp(vm, "alice", "alice password");
        vm.Dispose();

        // Next launch: alice's unlock screen offers another account.
        vm = new MainWindowViewModel(first, startPolling: false);
        var window = Show(vm);
        var unlock = Assert.IsType<UnlockViewModel>(vm.Current);
        Assert.Equal("Unlock alice", unlock.Heading);
        Assert.True(Find<Button>(window, "OtherAccountButton").IsVisible);
        Screenshot(window, "17-unlock-with-switch");

        Find<Button>(window, "OtherAccountButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var picker = Assert.IsType<AccountsViewModel>(vm.Current);
        Assert.Equal(["alice"], picker.Accounts.Select(a => a.Title));
        picker.CreateNewCommand.Execute(null);
        var signUp = Assert.IsType<OnboardingViewModel>(vm.Current);
        Assert.True(signUp.CanUseOtherAccount); // can go back to alice instead
        await SignUp(vm, "bob", "bob password");
        vm.Dispose();

        // The app reopens bob's account, the one used last.
        var reopened = AppOptions.Parse([], first.ProfilesRoot!);
        Assert.Equal("bob", reopened.AccountName);

        // From bob's unlock screen, switch to alice and unlock her.
        vm = new MainWindowViewModel(reopened with { HttpClientFactory = _relay.Factory.CreateClient, PasswordCost = PasswordCost.ForTests },
            startPolling: false);
        window = Show(vm);
        Assert.Equal("Unlock bob", Assert.IsType<UnlockViewModel>(vm.Current).Heading);
        Assert.IsType<UnlockViewModel>(vm.Current).UseOtherAccountCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        picker = Assert.IsType<AccountsViewModel>(vm.Current);
        Assert.Equal(["alice", "bob"], picker.Accounts.Select(a => a.Title));
        Assert.Equal("Password protected · last used", picker.Accounts[1].Detail);
        Screenshot(window, "18-choose-account");

        picker.Accounts[0].OpenCommand.Execute(null);
        unlock = Assert.IsType<UnlockViewModel>(vm.Current);
        Assert.Equal("Unlock alice", unlock.Heading);
        unlock.Password = "bob password";
        await unlock.UnlockCommand.ExecuteAsync(null);
        Assert.Equal("That password is incorrect.", unlock.Error); // each account has its own
        unlock.Password = "alice password";
        await unlock.UnlockCommand.ExecuteAsync(null);
        await vm.ShellReady;
        Assert.Equal("alice", Assert.IsType<ShellViewModel>(vm.Current).Me);
        Assert.Equal("alice", AppOptions.Parse([], first.ProfilesRoot!).AccountName);

        // Back from the picker returns to where you were.
        vm.Dispose();
        vm = new MainWindowViewModel(reopened with { HttpClientFactory = _relay.Factory.CreateClient }, startPolling: false);
        Assert.IsType<UnlockViewModel>(vm.Current).UseOtherAccountCommand.Execute(null);
        Assert.IsType<AccountsViewModel>(vm.Current).BackCommand.Execute(null);
        Assert.Equal("Unlock bob", Assert.IsType<UnlockViewModel>(vm.Current).Heading);
        vm.Dispose();
    }

    [Fact]
    public void ASingleDataDirectoryCannotSwitchAccounts()
    {
        var o = AppOptions.Parse(["--data-dir", _relay.ProfileDir("solo")], _relay.ProfileDir("unused-root"));
        Assert.False(o.CanSwitchAccounts);
        Assert.Empty(o.LocalAccounts());
    }

    [AvaloniaFact]
    public async Task ForgottenPasswordErasesAndStartsOver()
    {
        var options = Options("alice");
        var vm = new MainWindowViewModel(options, startPolling: false);
        var signUp = Assert.IsType<OnboardingViewModel>(vm.Current);
        (signUp.Username, signUp.Password, signUp.ConfirmPassword) = ("alice", "correct horse", "correct horse");
        await signUp.RegisterCommand.ExecuteAsync(null);
        await vm.ShellReady;
        vm.Dispose();

        vm = new MainWindowViewModel(options, startPolling: false);
        var window = Show(vm);
        var unlock = Assert.IsType<UnlockViewModel>(vm.Current);
        unlock.ForgotPasswordCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(unlock.ConfirmingErase);
        Screenshot(window, "16-forgot-password");
        unlock.CancelEraseCommand.Execute(null);
        Assert.False(unlock.ConfirmingErase);
        Assert.True(Directory.Exists(_relay.ProfileDir("alice")));

        unlock.ForgotPasswordCommand.Execute(null);
        unlock.EraseCommand.Execute(null);
        Assert.IsType<OnboardingViewModel>(vm.Current);
        Assert.False(Directory.Exists(_relay.ProfileDir("alice")));

        // The relay still holds the old name (the keys to close it are gone), so pick another.
        var again = Assert.IsType<OnboardingViewModel>(vm.Current);
        (again.Username, again.Password, again.ConfirmPassword) = ("alice", "new password", "new password");
        await again.RegisterCommand.ExecuteAsync(null);
        Assert.Contains("already registered", again.Error);
        again.Username = "alice2";
        await again.RegisterCommand.ExecuteAsync(null);
        Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task SignUpShowsRelayErrors()
    {
        await _relay.Register("alice");
        var vm = new MainWindowViewModel(Options("impostor"), startPolling: false);
        Show(vm);
        var onboarding = Assert.IsType<OnboardingViewModel>(vm.Current);

        (onboarding.Password, onboarding.ConfirmPassword) = ("correct horse", "correct horse");
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
        var bob = await _relay.Register("bob", friends: false);
        var carol = await _relay.Register("carol", friends: false);
        _relay.Close(await _relay.Register("alice", friends: false)); // alice signed up earlier; now she opens the app

        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        Assert.Empty(shell.Contacts);

        // Adding bob sends him a request; nothing can be sent until he accepts.
        Type(window, Find<TextBox>(window, "NewContactBox"), "bob", enter: true);
        await Settle();
        Assert.Equal("bob", shell.Selected?.Username);
        Assert.True(shell.ShowOutgoingRequest);
        Assert.False(shell.CanCompose);
        Assert.Equal("Request sent", shell.Selected!.Preview);
        Screenshot(window, "3a-request-sent");

        await bob.SyncAsync();
        Assert.Equal(ContactState.RequestReceived, bob.GetContact("alice")!.State);
        await bob.AcceptRequestAsync("alice");
        await shell.SyncNowAsync();
        Assert.False(shell.ShowOutgoingRequest);
        Assert.True(shell.CanCompose);
        Assert.Equal(["bob accepted your request."], shell.Messages.Select(m => m.Text));

        // Send with the Enter key, as a user would.
        Type(window, Find<TextBox>(window, "DraftBox"), "Hi Bob! This is end-to-end encrypted.", enter: true);
        await Settle();
        var sent = shell.Messages.Last();
        Assert.True(sent.Outgoing);
        Assert.Equal(MessageStatus.Sent, sent.Status);
        Assert.Equal("", shell.Draft);

        // Bob reads it and replies; alice's next poll shows the reply in the open chat.
        Assert.Equal(["Hi Bob! This is end-to-end encrypted."], (await bob.SyncAsync()).Received.Select(m => m.Text));
        await bob.SendAsync("alice", "Got it. The relay only saw ciphertext.");
        await bob.SendAsync("alice", "Want to verify safety numbers later?");
        await shell.SyncNowAsync();
        Assert.Equal(4, shell.Messages.Count);
        Assert.False(shell.Messages[2].Outgoing);
        Assert.Equal("Want to verify safety numbers later?", shell.Contacts.Single(c => c.Username == "bob").Preview);
        Assert.Equal(0, shell.Contacts.Single(c => c.Username == "bob").Unread); // the chat is open

        // A request from someone new puts them at the top of the list, marked as a request.
        await carol.AddContactAsync("alice");
        await Assert.ThrowsAsync<NotFriendsException>(() => carol.SendAsync("alice", "let me in"));
        await shell.SyncNowAsync();
        var carolRow = shell.Contacts[0];
        Assert.Equal("carol", carolRow.Username);
        Assert.True(carolRow.IsIncomingRequest);
        Assert.Equal(1, carolRow.Unread);
        Assert.Equal(4, shell.Messages.Count); // still showing bob's chat
        Screenshot(window, "3-chat");

        // Opening it shows Accept / Decline; accepting lets them talk.
        shell.Selected = carolRow;
        await Settle();
        Assert.Equal(0, carolRow.Unread);
        Assert.True(shell.ShowIncomingRequest);
        Assert.False(shell.CanCompose);
        Screenshot(window, "3b-incoming-request");
        Find<Button>(window, "AcceptRequestButton").Command!.Execute(null);
        await Settle();
        Assert.True(shell.CanCompose);
        Assert.False(shell.ShowIncomingRequest);
        await carol.SyncAsync();
        Assert.True(carol.GetContact("alice")!.IsFriend);
        await carol.SendAsync("alice", "hey alice, it's carol");
        await shell.SyncNowAsync();
        Assert.Equal("hey alice, it's carol", shell.Messages.Last().Text);

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
    public async Task DecliningARequestBlocksTheSender()
    {
        var mallory = await _relay.Register("mallory", friends: false);
        _relay.Close(await _relay.Register("alice", friends: false));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;

        await mallory.AddContactAsync("alice");
        await shell.SyncNowAsync();
        shell.Selected = Assert.Single(shell.Contacts);
        await Settle();
        Find<Button>(window, "DeclineRequestButton").Command!.Execute(null);
        await Settle();
        Assert.Empty(shell.Contacts);
        Assert.Null(shell.Selected);

        // Another request, or a sneaky direct message, never shows up.
        await mallory.AddContactAsync("alice");
        await shell.SyncNowAsync();
        Assert.Empty(shell.Contacts);
        vm.Dispose();
    }

    private long RelayCount(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_relay.RelayDbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    [AvaloniaFact]
    public async Task LogOutErasesTheAccountAndFreesTheUsername()
    {
        var bob = await _relay.Register("bob");
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        Type(window, Find<TextBox>(window, "NewContactBox"), "bob", enter: true);
        await Settle();
        Type(window, Find<TextBox>(window, "DraftBox"), "bye for now", enter: true);
        await Settle();
        await bob.SendAsync("alice", "still queued on the relay when alice logs out");
        var aliceKey = (await bob.CheckIdentityAsync("alice")).SafetyNumber;

        // Cancel keeps everything.
        Find<Button>(window, "LogOutButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.IsLogOutOpen);
        Screenshot(window, "14-log-out");
        shell.CancelLogOutCommand.Execute(null);
        Assert.False(shell.IsLogOutOpen);
        Assert.IsType<ShellViewModel>(vm.Current);

        // Confirming closes the account on the relay and erases this computer's copy.
        shell.OpenLogOutCommand.Execute(null);
        await shell.LogOutCommand.ExecuteAsync(null);
        var onboarding = Assert.IsType<OnboardingViewModel>(vm.Current);
        Assert.Equal("E2EE Messenger", vm.Title);
        Assert.False(Directory.Exists(_relay.ProfileDir("alice")));
        Assert.Equal(0, RelayCount("SELECT count(*) FROM accounts WHERE username = 'alice'"));
        Assert.Equal(0, RelayCount("SELECT count(*) FROM inbox WHERE recipient = 'alice'"));
        Assert.Equal(0, RelayCount("SELECT count(*) FROM one_time_prekeys WHERE username = 'alice'"));
        Assert.Equal(1, RelayCount("SELECT count(*) FROM accounts WHERE username = 'bob'"));

        // The username is free again; whoever takes it has new keys, and bob can tell.
        Show(vm);
        (onboarding.Username, onboarding.Password, onboarding.ConfirmPassword) = ("alice", "correct horse", "correct horse");
        await onboarding.RegisterCommand.ExecuteAsync(null);
        var fresh = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        Assert.Empty(fresh.Contacts);
        var check = await bob.CheckIdentityAsync("alice");
        Assert.False(check.MatchesTrustedKey);
        Assert.NotEqual(aliceKey, check.SafetyNumber);
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task LogOutOffersToContinueWhenTheRelayCannotCloseTheAccount()
    {
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;

        // The relay already forgot alice (as an old relay that lacks the endpoint, or a wiped one, would fail).
        using (var c = new SqliteConnection($"Data Source={_relay.RelayDbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM accounts WHERE username = 'alice'";
            cmd.ExecuteNonQuery();
        }
        shell.OpenLogOutCommand.Execute(null);
        await shell.LogOutCommand.ExecuteAsync(null);
        Assert.True(shell.HasLogOutError);
        Assert.Contains("username stays taken", shell.LogOutError);
        Assert.IsType<ShellViewModel>(vm.Current);
        Assert.True(Directory.Exists(_relay.ProfileDir("alice")));

        shell.LogOutAnywayCommand.Execute(null);
        Assert.IsType<OnboardingViewModel>(vm.Current);
        Assert.False(Directory.Exists(_relay.ProfileDir("alice")));
        vm.Dispose();
    }

    [Fact]
    public void SignUpDefaultsToTheBuiltInRelay()
    {
        Assert.Equal("https://signal-clone.duckdns.org:8443", AppOptions.BuiltInRelay);
        var previous = Environment.GetEnvironmentVariable("E2EE_SERVER");
        try
        {
            Environment.SetEnvironmentVariable("E2EE_SERVER", null);
            Assert.Equal(AppOptions.BuiltInRelay, AppOptions.Parse([]).DefaultServer);
            Environment.SetEnvironmentVariable("E2EE_SERVER", "http://127.0.0.1:8000");
            Assert.Equal("http://127.0.0.1:8000", AppOptions.Parse([]).DefaultServer);
            Assert.Equal("http://10.0.0.5:8000", AppOptions.Parse(["--server", "http://10.0.0.5:8000"]).DefaultServer);
        }
        finally { Environment.SetEnvironmentVariable("E2EE_SERVER", previous); }
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
        Assert.False(shell.CanCompose);
        Assert.False(shell.SendCommand.CanExecute(null));
        Assert.True(Find<Button>(window, "ReviewKeyButton").IsEffectivelyVisible);
        Screenshot(window, "4-identity-changed");
        vm.Dispose();
    }

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

    private static string Joined(SafetyNumberViewModel s) => string.Join(" ", s.Rows).Replace("  ", " ");

    [AvaloniaFact]
    public async Task VerifySafetyNumber()
    {
        var bob = await _relay.Register("bob");
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        shell.NewContact = "bob";
        await shell.AddContactCommand.ExecuteAsync(null);
        shell.Draft = "let's compare safety numbers";
        await shell.SendCommand.ExecuteAsync(null);
        await bob.SyncAsync();

        await shell.ShowSafetyNumberCommand.ExecuteAsync(null);
        var panel = Assert.IsType<SafetyNumberViewModel>(shell.Safety);
        Assert.True(panel.IsCurrentKey);
        Assert.Equal(await bob.SafetyNumberAsync("alice"), Joined(panel)); // what bob sees on his screen
        Screenshot(window, "5-safety-number");

        await panel.MarkVerifiedCommand.ExecuteAsync(null);
        Assert.Null(shell.Safety);
        Assert.True(shell.Selected!.Verified);
        Screenshot(window, "6-verified");
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task ReviewAndTrustAChangedKey()
    {
        var bob = await _relay.Register("bob");
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        shell.NewContact = "bob";
        await shell.AddContactCommand.ExecuteAsync(null);
        shell.Draft = "hi bob";
        await shell.SendCommand.ExecuteAsync(null);

        var newBob = await ReinstallBob();
        await newBob.SendAsync("alice", "new laptop, same bob");
        await shell.SyncNowAsync();

        var contact = shell.Selected!;
        Assert.True(contact.IdentityChanged);
        Assert.False(shell.CanCompose);
        Assert.Single(shell.Messages); // the new message is on hold
        Screenshot(window, "7-key-changed");

        await shell.ShowSafetyNumberCommand.ExecuteAsync(null);
        var panel = Assert.IsType<SafetyNumberViewModel>(shell.Safety);
        Assert.True(panel.IsKeyChange);
        Assert.Equal(await newBob.SafetyNumberAsync("alice"), Joined(panel));
        Screenshot(window, "8-review-new-key");

        await panel.TrustAndVerifyCommand.ExecuteAsync(null);
        Assert.Null(shell.Safety);
        Assert.False(contact.IdentityChanged);
        Assert.True(contact.Verified);
        Assert.True(shell.CanCompose);
        Assert.Equal(["hi bob", "new laptop, same bob"], shell.Messages.Select(m => m.Text));
        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task GroupChat()
    {
        var bob = await _relay.Register("bob");
        var carol = await _relay.Register("carol");
        await _relay.Register("dave");
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;

        // Create the group the way a user would.
        Find<Button>(window, "NewGroupButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var form = Assert.IsType<GroupFormViewModel>(shell.GroupForm);
        Assert.False(form.SubmitCommand.CanExecute(null));
        Type(window, Find<TextBox>(window, "GroupNameBox"), "COSC 483 project");
        Type(window, Find<TextBox>(window, "GroupMembersBox"), "bob, carol, nobody");
        await form.SubmitCommand.ExecuteAsync(null);
        Assert.Contains("only add friends", form.Error); // the form stays open with the error
        Assert.Contains("nobody", form.Error);
        Screenshot(window, "9-new-group");

        form.Members = "bob, carol";
        await form.SubmitCommand.ExecuteAsync(null);
        Assert.Null(shell.GroupForm);
        var group = shell.Selected!;
        Assert.True(group.IsGroup);
        Assert.Equal("COSC 483 project", group.Title);
        Assert.Equal("You, bob, carol", group.MembersText);
        Assert.True(shell.CanManageGroup);
        Assert.False(shell.ShowSafetyButton);

        Type(window, Find<TextBox>(window, "DraftBox"), "Report draft is in the repo.", enter: true);
        await Settle();
        Assert.Equal(MessageStatus.Sent, shell.Messages.Last().Status);

        await bob.SyncAsync();
        await carol.SyncAsync();
        await bob.SendGroupAsync(group.GroupId!, "I'll take the Background section.");
        await carol.SendGroupAsync(group.GroupId!, "Design and testing for me.");
        await shell.SyncNowAsync();
        Assert.Equal(["bob", "carol"], shell.Messages.Where(m => m.ShowSender).Select(m => m.Sender));
        Assert.Equal("carol: Design and testing for me.", group.Preview);

        // Each person's name has their own color, the same on every message.
        await bob.SendGroupAsync(group.GroupId!, "Also the intro.");
        await shell.SyncNowAsync();
        var names = shell.Messages.Where(m => m.ShowSender).ToList();
        Assert.Equal(2, names.Select(m => m.SenderBrush).Distinct().Count());
        Assert.Equal(names[0].SenderBrush, names[2].SenderBrush);

        // Adding someone shows up as a line in the chat and in the header.
        shell.OpenAddMembersCommand.Execute(null);
        shell.GroupForm!.Members = "dave";
        await shell.GroupForm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("You, bob, carol, dave", group.MembersText);
        Assert.Equal("You added dave.", shell.Messages.Last().Text);
        Assert.True(shell.Messages.Last().IsEvent);
        Screenshot(window, "10-group-chat");

        // Dark mode switches to the light-on-dark palette; people keep their slots.
        var bobSlot = names[0].SenderSlot;
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SenderColors.Dark[bobSlot], ((Avalonia.Media.ISolidColorBrush)names[0].SenderBrush!).Color.ToString().Replace("#ff", "#").ToUpperInvariant());
        Screenshot(window, "11-group-chat-dark");
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Dispatcher.UIThread.RunJobs();

        // A group someone else creates appears with an unread badge.
        var other = await bob.CreateGroupAsync("Lunch", ["alice"]);
        await shell.SyncNowAsync();
        Assert.Equal("Lunch", shell.Contacts[0].Title);
        Assert.Equal(1, shell.Contacts[0].Unread);

        // Leaving keeps the history but closes the composer.
        shell.Selected = shell.Contacts[0];
        await Settle();
        await shell.LeaveGroupCommand.ExecuteAsync(null);
        Assert.True(shell.Selected.HasLeft);
        Assert.False(shell.CanCompose);
        Assert.False(shell.CanManageGroup);
        Assert.Equal("You left the group.", shell.Messages.Last().Text);
        Assert.Equal(["* alice left."], (await bob.SyncAsync()).Received.Where(m => m.GroupId == other.Id).Select(m => $"* {m.Text}"));

        // Groups come back after a restart.
        vm.Dispose();
        var reopened = new MainWindowViewModel(Options("alice"), startPolling: false);
        var shell2 = Assert.IsType<ShellViewModel>(reopened.Current);
        await reopened.ShellReady;
        Assert.Contains(shell2.Contacts, c => c.Title == "COSC 483 project" && c.Active);
        Assert.Contains(shell2.Contacts, c => c.Title == "Lunch" && !c.Active);
        reopened.Dispose();
    }

    [AvaloniaFact]
    public void SenderColorsStandOutInBothThemes()
    {
        static double Luminance(Avalonia.Media.Color c)
        {
            static double Channel(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
        static double Contrast(Avalonia.Media.Color a, Avalonia.Media.Color b)
        {
            var (x, y) = (Luminance(a), Luminance(b));
            return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
        }

        foreach (var (theme, palette, window) in new[]
                 {
                     (Avalonia.Styling.ThemeVariant.Light, SenderColors.Light, Avalonia.Media.Colors.White),
                     (Avalonia.Styling.ThemeVariant.Dark, SenderColors.Dark, Avalonia.Media.Color.Parse("#1C1C1E")),
                 })
        {
            Assert.True(Application.Current!.TryGetResource("IncomingBubbleBrush", theme, out var res));
            var bubble = ((Avalonia.Media.ISolidColorBrush)res!).Color;
            Assert.Equal(SenderColors.Count, palette.Distinct().Count());
            Assert.True(Application.Current.TryGetResource("LinkBrush", theme, out var link));
            // The sender names, plus the "Show more" link color.
            string[] colors = [.. palette, ((Avalonia.Media.ISolidColorBrush)link!).Color.ToString().Replace("#ff", "#")];
            foreach (var hex in colors)
            {
                var c = Avalonia.Media.Color.Parse(hex);
                Assert.True(Contrast(c, bubble) >= 4.5, $"{hex} on the {theme} bubble: {Contrast(c, bubble):F2}:1");
                Assert.True(Contrast(c, window) >= 4.5, $"{hex} on the {theme} window: {Contrast(c, window):F2}:1");
            }
        }
    }

    /// <summary>A real PNG, so the app has something to decode and show.</summary>
    private static string SunsetPng(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "sunset.png");
        using var bmp = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(480, 300));
        using (var ctx = bmp.CreateDrawingContext())
        {
            ctx.FillRectangle(new Avalonia.Media.LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new(Avalonia.Media.Color.Parse("#3A1C71"), 0), new(Avalonia.Media.Color.Parse("#D76D77"), 0.6), new(Avalonia.Media.Color.Parse("#FFAF7B"), 1) },
            }, new Rect(0, 0, 480, 300));
            ctx.DrawEllipse(Avalonia.Media.Brushes.Gold, null, new Point(240, 230), 70, 70);
            ctx.FillRectangle(new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#1D3557")), new Rect(0, 230, 480, 70));
        }
        bmp.Save(path);
        return path;
    }

    [AvaloniaFact]
    public async Task PhotosVideosAndReactions()
    {
        var bob = await _relay.Register("bob");
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        var files = Path.Combine(Path.GetTempPath(), $"e2ee-app-media-{Guid.NewGuid():N}");
        var opened = new List<string>();
        shell.OpenFile = opened.Add;
        shell.DownloadsFolder = Path.Combine(files, "Downloads");

        shell.NewContact = "bob";
        await shell.AddContactCommand.ExecuteAsync(null);
        shell.Draft = "Sunset from the lab roof";
        await shell.SendFileAsync(SunsetPng(files));
        await Settle();
        var photo = Assert.Single(shell.Messages);
        Assert.True(photo.HasImage); // decrypted from this device's copy and shown inline
        Assert.Equal("Sunset from the lab roof", photo.Text);
        Assert.Equal("", shell.Draft);
        Assert.Equal("You: 📷 Sunset from the lab roof", shell.Selected!.Preview);

        // Bob gets it, loves it, and replies with a video.
        var atBob = Assert.Single((await bob.SyncAsync()).Received);
        Assert.Equal(AttachmentState.Ready, atBob.Attachment!.State);
        await bob.ReactAsync(atBob.Id, "❤️");
        var clip = Path.Combine(files, "timelapse.mp4");
        File.WriteAllBytes(clip, new byte[150_000]);
        await bob.SendFileAsync("alice", clip, "timelapse!");
        await shell.SyncNowAsync();
        await Settle();
        Assert.Equal("❤️", photo.ReactionsText);
        var video = shell.Messages[1];
        Assert.True(video.ShowFileCard);
        Assert.Equal("🎬  timelapse.mp4 · 146 KB", video.FileLabel);

        // Alice reacts; picking the same reaction again takes it back.
        await video.ReactCommand.ExecuteAsync("😂");
        Assert.Equal("😂", video.MyReaction);
        Assert.Equal([new Reaction("alice", "😂")], (await bob.SyncAsync()).Updated.Single().Reactions);
        await video.ReactCommand.ExecuteAsync("😂");
        Assert.False(video.HasReactions);
        await video.ReactCommand.ExecuteAsync("👍");

        // Open hands the system a plaintext copy with a safe extension; Save keeps one in Downloads.
        await video.OpenCommand.ExecuteAsync(null);
        Assert.Equal(".mp4", Path.GetExtension(Assert.Single(opened)));
        await video.SaveCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(shell.DownloadsFolder, "timelapse.mp4")));
        Assert.Contains("Saved to Downloads", shell.Notice);
        shell.DismissNoticeCommand.Execute(null);
        Screenshot(window, "12-photos-and-reactions");

        // Not images or videos: refused with a notice, draft kept.
        var script = Path.Combine(files, "notes.sh");
        File.WriteAllText(script, "echo hi");
        shell.Draft = "keep me";
        await shell.SendFileAsync(script);
        Assert.Contains("can't be sent", shell.Notice);
        Assert.Equal("keep me", shell.Draft);
        vm.Dispose();
        Directory.Delete(files, recursive: true);
    }

    [AvaloniaFact]
    public async Task LongMessagesCollapseAndDocumentsShowAsCards()
    {
        var bob = await _relay.Register("bob");
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: false);
        var window = Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;
        shell.NewContact = "bob";
        await shell.AddContactCommand.ExecuteAsync(null);
        Assert.Equal(Messenger.MaxTextLength, Find<TextBox>(window, "DraftBox").MaxLength);

        var essay = string.Join(" ", Enumerable.Range(1, 400).Select(i => $"word{i}"));
        await bob.SendAsync("alice", essay);
        await bob.SendAsync("alice", "short one");
        var files = Path.Combine(Path.GetTempPath(), $"e2ee-app-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(files);
        File.WriteAllBytes(Path.Combine(files, "Project report draft.pdf"), new byte[52_000]);
        await bob.SendFileAsync("alice", Path.Combine(files, "Project report draft.pdf"), "comments welcome");
        await shell.SyncNowAsync();
        await Settle();

        var (longOne, shortOne, doc) = (shell.Messages[0], shell.Messages[1], shell.Messages[2]);
        Assert.True(longOne.IsLong);
        Assert.False(shortOne.IsLong);
        Assert.EndsWith("…", longOne.DisplayText);
        Assert.True(longOne.DisplayText.Length <= MessageViewModel.CollapseChars + 1);
        Assert.Equal("Show more", longOne.ShowMoreText);
        Assert.True(Find<Button>(window, "ShowMoreButton").IsEffectivelyVisible);

        Assert.True(doc.ShowFileCard);
        Assert.Equal("📄  Project report draft.pdf · 51 KB", doc.FileLabel);
        Assert.Equal("📄 comments welcome", shell.Selected!.Preview);
        Screenshot(window, "13-long-message-and-document");

        longOne.ToggleExpandedCommand.Execute(null);
        Assert.Equal(essay, longOne.DisplayText);
        Assert.Equal("Show less", longOne.ShowMoreText);
        longOne.ToggleExpandedCommand.Execute(null);
        Assert.EndsWith("…", longOne.DisplayText);

        // Many short lines collapse too.
        var lines = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}"));
        Assert.True(new MessageViewModel(new StoredMessage(1, "bob", false, lines, DateTimeOffset.Now, MessageStatus.Received)).IsLong);
        vm.Dispose();
        Directory.Delete(files, recursive: true);
    }

    [AvaloniaFact]
    public async Task PushDeliversWithoutPolling()
    {
        var bob = await _relay.Register("bob");
        _relay.Close(await _relay.Register("alice"));
        var vm = new MainWindowViewModel(Options("alice"), startPolling: true);
        Show(vm);
        var shell = Assert.IsType<ShellViewModel>(vm.Current);
        await vm.ShellReady;

        await bob.SendAsync("alice", "pushed");
        for (var i = 0; i < 100 && shell.Contacts.FirstOrDefault()?.Preview != "pushed"; i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.Equal("pushed", Assert.Single(shell.Contacts).Preview);
        vm.Dispose();
    }
}
