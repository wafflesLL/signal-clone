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
    private readonly AppOptions _options;

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
            var m = Messenger.Open(_options.ToMessengerOptions(null));
            if (m.IsRegistered) return ShowShell(m);
            m.Dispose();
            return new OnboardingViewModel(_options, OnRegistered);
        }
        catch (NoRelayConfiguredException)
        {
            return new OnboardingViewModel(_options, OnRegistered);
        }
        catch (MessengerException e)
        {
            return new ErrorViewModel(e.Message);
        }
    }

    private void OnRegistered(Messenger m) => Current = ShowShell(m);

    private ShellViewModel ShowShell(Messenger m)
    {
        Title = $"E2EE Messenger — {m.Username}";
        var shell = new ShellViewModel(m);
        ShellReady = shell.StartAsync(StartPolling);
        return shell;
    }

    public void Dispose()
    {
        if (Current is IDisposable d) d.Dispose();
    }
}
