using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E2ee.Messaging;
using E2ee.Wire;

namespace E2ee.App.ViewModels;

/// <summary>First run: pick a username, a password that locks the app on this computer, and a relay.
/// Keys are generated and uploaded for you.</summary>
public sealed partial class OnboardingViewModel(AppOptions options, Action<Messenger> onRegistered) : ViewModelBase
{
    /// <summary>Opens the account picker; null hides the option.</summary>
    public Action? OtherAccounts { get; init; }
    public bool CanUseOtherAccount => OtherAccounts is not null;

    [RelayCommand] private void UseOtherAccount() => OtherAccounts?.Invoke();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _username = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _serverUrl = options.DefaultServer;

    /// <summary>Locks the app on this computer. Never sent anywhere.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _password = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _confirmPassword = "";

    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private bool _isBusy;

    private bool CanRegister() => !IsBusy && Username.Trim().Length > 0 && ServerUrl.Trim().Length > 0
        && Password.Length > 0 && ConfirmPassword.Length > 0;

    [RelayCommand(CanExecute = nameof(CanRegister))]
    private async Task RegisterAsync()
    {
        Error = null;
        var name = Username.Trim().ToLowerInvariant();
        if (!Usernames.IsValid(name))
        {
            Error = "Usernames are 3–32 characters: lowercase letters, digits, and underscores.";
            return;
        }
        if (PasswordMasterKeyStore.CheckNewPassword(Password, ConfirmPassword) is { } problem)
        {
            Error = problem;
            return;
        }
        if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https"))
        {
            Error = $"Enter the relay's address, like {AppOptions.BuiltInRelay}.";
            return;
        }

        IsBusy = true;
        Messenger? m = null;
        try
        {
            var password = Password;
            m = await Task.Run(() => Messenger.Open(options.ToMessengerOptions(server, password)));
            await m.RegisterAsync(name);
            onRegistered(m);
            m = null;
            Password = ConfirmPassword = "";
        }
        catch (RelayException e) when (e.Status is null)
        {
            Error = $"Can't reach the relay at {server}. Is it running?";
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            Error = e.Message;
        }
        finally
        {
            if (m is not null)
            {
                // Didn't register: erase the half-made profile so the next try starts clean
                // (possibly with a different password).
                m.Dispose();
                try { Messenger.DeleteProfile(options.ToMessengerOptions(null, "")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or MessengerException) { }
            }
            IsBusy = false;
        }
    }
}
