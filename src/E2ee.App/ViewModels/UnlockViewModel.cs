using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E2ee.Messaging;

namespace E2ee.App.ViewModels;

/// <summary>Opening a password-locked profile. The password unlocks the key that decrypts everything stored here.</summary>
public sealed partial class UnlockViewModel(AppOptions options, Action<Messenger> onUnlocked, Action onErased) : ViewModelBase
{
    /// <summary>Opens the account picker; null hides the option.</summary>
    public Action? OtherAccounts { get; init; }
    public bool CanUseOtherAccount => OtherAccounts is not null;

    /// <summary>Which account this is, from its folder (the username is otherwise encrypted).</summary>
    public string Heading => options.AccountName is { } name ? $"Unlock {name}" : "Unlock";

    [RelayCommand] private void UseOtherAccount() => OtherAccounts?.Invoke();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    private bool _isBusy;

    /// <summary>"Forgot password?" was clicked: show what erasing means before doing it.</summary>
    [ObservableProperty] private bool _confirmingErase;

    private bool CanUnlock() => !IsBusy && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private async Task UnlockAsync()
    {
        Error = null;
        IsBusy = true;
        var password = Password;
        try
        {
            var m = await Task.Run(() => Messenger.Open(options.ToMessengerOptions(null, password)));
            Password = "";
            onUnlocked(m);
        }
        catch (WrongPasswordException e)
        {
            Error = e.Message;
            Password = "";
        }
        catch (MessengerException e)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand] private void ForgotPassword() => ConfirmingErase = true;
    [RelayCommand] private void CancelErase() => ConfirmingErase = false;

    /// <summary>
    /// Without the password the keys can't be decrypted, so the relay can't be asked to
    /// close the account either; the username stays taken there.
    /// </summary>
    [RelayCommand]
    private void Erase()
    {
        try
        {
            Messenger.DeleteProfile(options.ToMessengerOptions(null, ""));
            onErased();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or MessengerException)
        {
            Error = $"Couldn't erase the profile: {e.Message}";
        }
    }
}
