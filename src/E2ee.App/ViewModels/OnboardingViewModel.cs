using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E2ee.Messaging;
using E2ee.Wire;

namespace E2ee.App.ViewModels;

/// <summary>First run: pick a username and a relay. Keys are generated and uploaded for you.</summary>
public sealed partial class OnboardingViewModel(AppOptions options, Action<Messenger> onRegistered) : ViewModelBase
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _username = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private string _serverUrl = options.DefaultServer;

    [ObservableProperty] private string? _error;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private bool _isBusy;

    private bool CanRegister() => !IsBusy && Username.Trim().Length > 0 && ServerUrl.Trim().Length > 0;

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
        if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var server) || server.Scheme is not ("http" or "https"))
        {
            Error = "Enter the relay's address, like http://127.0.0.1:8000.";
            return;
        }

        IsBusy = true;
        Messenger? m = null;
        try
        {
            m = await Task.Run(() => Messenger.Open(options.ToMessengerOptions(server)));
            await m.RegisterAsync(name);
            onRegistered(m);
            m = null;
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
            m?.Dispose();
            IsBusy = false;
        }
    }
}
