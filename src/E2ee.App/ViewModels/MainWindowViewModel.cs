using CommunityToolkit.Mvvm.ComponentModel;
using E2ee.Messaging;

namespace E2ee.App.ViewModels;

public abstract class ViewModelBase : ObservableObject;

public sealed class ErrorViewModel(string message) : ViewModelBase
{
    public string Message { get; } = message;
}

/// <summary>Picks the first screen: sign-up for a new profile, otherwise the chat shell.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private AppOptions _options;

    [ObservableProperty] private ViewModelBase _current;
    [ObservableProperty] private string _title = "E2EE Messenger";

    public MainWindowViewModel(AppOptions options, bool startPolling = true)
    {
        _options = options;
        StartPolling = startPolling;
        _current = Start();
    }

    /// <summary>False in tests, which call SyncNowAsync themselves.</summary>
    public bool StartPolling { get; }

    /// <summary>Completes when the shell has loaded contacts and done its first sync.</summary>
    public Task ShellReady { get; private set; } = Task.CompletedTask;

    private ViewModelBase Start()
    {
        try
        {
            if (_options.IsLocked)
                return new UnlockViewModel(_options, OnRegistered, ShowSignUp) { OtherAccounts = OtherAccountsAction() };
            if (!_options.HasProfile) return SignUp();

            // A profile from before passwords (or from tests): the master key is in the Keychain.
            var m = Messenger.Open(_options.ToMessengerOptions(null));
            if (m.IsRegistered) return ShowShell(m);
            // A sign-up that never finished: start over, with a password this time.
            m.Dispose();
            Messenger.DeleteProfile(_options.ToMessengerOptions(null));
            return SignUp();
        }
        catch (NoRelayConfiguredException)
        {
            return SignUp();
        }
        catch (MessengerException e)
        {
            return new ErrorViewModel(e.Message);
        }
    }

    private OnboardingViewModel SignUp() => new(_options, OnRegistered)
    {
        // Only offered when there's another account here to go to.
        OtherAccounts = _options.LocalAccounts().Any(a => a.Options.DataDirectory != _options.DataDirectory) ? OtherAccountsAction() : null,
    };

    // ---------------------------------------------------------------- other accounts

    /// <summary>Null when this window can't switch (a single --data-dir).</summary>
    private Action? OtherAccountsAction() => _options.CanSwitchAccounts ? ShowAccounts : null;

    /// <summary>The account picker: every account on this computer, plus "create a new account".</summary>
    public void ShowAccounts()
    {
        Title = "E2EE Messenger";
        var current = _options;
        Current = new AccountsViewModel(_options.LocalAccounts(), _options.DataDirectory,
            pick: a => SwitchTo(a.Options),
            createNew: () => SwitchTo(current.ForNewAccount()),
            back: () => SwitchTo(current));
    }

    /// <summary>Closes whatever is open and starts over on another account's folder.</summary>
    private void SwitchTo(AppOptions options)
    {
        if (Current is IDisposable d) d.Dispose();
        _options = options;
        Current = Start();
    }

    private void ShowSignUp()
    {
        Title = "E2EE Messenger";
        Current = SignUp();
    }

    private void OnRegistered(Messenger m) => Current = ShowShell(m);

    private ShellViewModel ShowShell(Messenger m)
    {
        Title = $"E2EE Messenger — {m.Username}";
        _options.Remember(m.Username!);
        var shell = new ShellViewModel(m);
        shell.LoggedOut += () => LogOut(shell);
        ShellReady = shell.StartAsync(StartPolling);
        return shell;
    }

    /// <summary>Closes the profile, erases it from this computer, and goes back to sign-up.</summary>
    private void LogOut(ShellViewModel shell)
    {
        shell.Dispose();
        Title = "E2EE Messenger";
        try
        {
            Messenger.DeleteProfile(_options.ToMessengerOptions(null));
            Current = SignUp();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or MessengerException)
        {
            Current = new ErrorViewModel($"Logged out, but this computer's copy couldn't be fully erased: {e.Message}");
        }
    }

    public void Dispose()
    {
        if (Current is IDisposable d) d.Dispose();
    }
}
