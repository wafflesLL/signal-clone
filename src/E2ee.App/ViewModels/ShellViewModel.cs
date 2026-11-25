using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E2ee.Messaging;

namespace E2ee.App.ViewModels;

/// <summary>The main screen: contact list on the left, the open conversation on the right.
/// Every action goes through <see cref="Messenger"/>; nothing here touches crypto.</summary>
public sealed partial class ShellViewModel(Messenger messenger) : ViewModelBase, IDisposable
{
    /// <summary>How long one push wait may stay open on the relay.</summary>
    public static readonly TimeSpan PushWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stop = new();
    private bool _syncing;

    public string Me => messenger.Username!;
    public string ServerText => messenger.Server.Authority;

    public ObservableCollection<ContactViewModel> Contacts { get; } = new();
    public ObservableCollection<MessageViewModel> Messages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanCompose))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(ShowSafetyNumberCommand))]
    private ContactViewModel? _selected;

    public bool HasSelection => Selected is not null;

    /// <summary>False while the open contact's key change is unresolved.</summary>
    public bool CanCompose => Selected is { IdentityChanged: false };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddContactCommand))]
    private string _newContact = "";

    /// <summary>A banner for errors and dropped messages. Null hides it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    public bool HasNotice => Notice is not null;

    [ObservableProperty] private bool _relayOnline = true;

    /// <summary>The safety-number panel, when open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSafetyOpen))]
    private SafetyNumberViewModel? _safety;

    public bool IsSafetyOpen => Safety is not null;

    public async Task StartAsync(bool push = true)
    {
        foreach (var c in await messenger.ConversationsAsync())
            Contacts.Add(new ContactViewModel(c.Contact, c.Last));
        await SyncNowAsync();
        if (push) _ = PushLoopAsync(_stop.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        messenger.Dispose();
    }

    /// <summary>
    /// Holds a wait open on the relay, which answers the moment mail arrives, then syncs.
    /// Also syncs at least every <see cref="PushWait"/>, which retries queued messages.
    /// Backs off while the relay is unreachable.
    /// </summary>
    private async Task PushLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await messenger.WaitForMessagesAsync(PushWait, ct);
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (RelayException)
            {
                RelayOnline = false;
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return; // the app is closing
            }
            if (ct.IsCancellationRequested) return;
            await SyncNowAsync();
        }
    }

    partial void OnSelectedChanged(ContactViewModel? value) => _ = LoadConversationAsync(value);

    private async Task LoadConversationAsync(ContactViewModel? contact)
    {
        Messages.Clear();
        Safety = null;
        if (contact is null) return;
        contact.Unread = 0;
        var history = await messenger.HistoryAsync(contact.Username, 200);
        if (Selected != contact) return; // the user clicked elsewhere meanwhile
        Messages.Clear();
        foreach (var m in history) Messages.Add(new MessageViewModel(m));
    }

    private bool CanSend() => CanCompose && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var peer = Selected!;
        var text = Draft.Trim();
        Draft = "";
        try
        {
            var msg = await messenger.SendAsync(peer.Username, text);
            if (Selected == peer) Messages.Add(new MessageViewModel(msg));
            peer.Update(msg);
            MoveToTop(peer);
            RelayOnline = msg.Status != MessageStatus.Pending;
        }
        catch (IdentityChangedException)
        {
            MarkIdentityChanged(peer.Username);
            Draft = text;
        }
        catch (RelayException e) when (e.Status is null)
        {
            RelayOnline = false;
            Notice = $"Can't reach the relay, so a conversation with {peer.Username} can't start yet. Try again when it's back.";
            Draft = text;
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            Notice = e.Message;
            Draft = text;
        }
    }

    private bool CanAddContact() => !string.IsNullOrWhiteSpace(NewContact);

    [RelayCommand(CanExecute = nameof(CanAddContact))]
    private async Task AddContactAsync()
    {
        var name = NewContact.Trim().ToLowerInvariant();
        if (Contacts.FirstOrDefault(c => c.Username == name) is { } existing)
        {
            Selected = existing;
            NewContact = "";
            return;
        }
        try
        {
            var contact = await messenger.AddContactAsync(name);
            var vm = new ContactViewModel(contact);
            Contacts.Insert(0, vm);
            Selected = vm;
            NewContact = "";
        }
        catch (RelayException e) when (e.Status is null)
        {
            RelayOnline = false;
            Notice = "Can't reach the relay to look that user up.";
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            Notice = e.Message;
        }
    }

    // ---------------------------------------------------------------- safety numbers

    private bool CanShowSafetyNumber() => Selected is not null;

    /// <summary>Opens the panel: the current number, or, after a key change, the new key's number to review.</summary>
    [RelayCommand(CanExecute = nameof(CanShowSafetyNumber))]
    private async Task ShowSafetyNumberAsync()
    {
        var peer = Selected!;
        try
        {
            Safety = peer.IdentityChanged
                ? SafetyNumberViewModel.ForKeyChange(this, peer, await messenger.CheckIdentityAsync(peer.Username))
                : SafetyNumberViewModel.ForCurrentKey(this, peer, await messenger.SafetyNumberAsync(peer.Username));
        }
        catch (RelayException e) when (e.Status is null)
        {
            RelayOnline = false;
            Notice = "Can't reach the relay to fetch the new key. Try again when it's back.";
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            Notice = e.Message;
        }
    }

    internal async Task SetVerifiedAsync(ContactViewModel peer, bool verified)
    {
        try
        {
            await messenger.SetVerifiedAsync(peer.Username, verified);
            peer.Verified = verified;
            Safety = null;
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            Notice = e.Message;
        }
    }

    internal async Task TrustAsync(ContactViewModel peer, IdentityCheck check, bool verified)
    {
        try
        {
            await messenger.TrustIdentityAsync(check, verified);
            peer.IdentityChanged = false;
            peer.Verified = verified;
            Safety = null;
            Notice = null;
            OnPropertyChanged(nameof(CanCompose));
            SendCommand.NotifyCanExecuteChanged();
            await SyncNowAsync(); // release messages that were on hold
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            Notice = e.Message;
        }
    }

    internal void CloseSafety() => Safety = null;

    private void MarkIdentityChanged(string username)
    {
        if (Contacts.FirstOrDefault(c => c.Username == username) is not { } c) return;
        c.IdentityChanged = true;
        c.Verified = false;
        if (c == Selected)
        {
            OnPropertyChanged(nameof(CanCompose));
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    // ---------------------------------------------------------------- delivery

    /// <summary>One round of delivery: retry queued messages, fetch new ones, update the screen.</summary>
    public async Task SyncNowAsync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var result = await messenger.SyncAsync();
            RelayOnline = true;
            foreach (var msg in result.Received)
            {
                var contact = Contacts.FirstOrDefault(c => c.Username == msg.Peer);
                if (contact is null)
                {
                    contact = new ContactViewModel(msg.Peer);
                    Contacts.Insert(0, contact);
                }
                contact.Update(msg);
                MoveToTop(contact);
                if (Selected == contact) Messages.Add(new MessageViewModel(msg));
                else contact.Unread++;
            }
            foreach (var user in result.IdentityChanges) MarkIdentityChanged(user);
            var problems = result.Problems.Where(p => !result.IdentityChanges.Any(u => p.StartsWith(u + "'s safety number"))).ToList();
            if (problems.Count > 0) Notice = string.Join("\n", problems);
            if (Messages.Any(m => m.IsPending) && Selected is { } open) await RefreshStatusesAsync(open);
        }
        catch (RelayException)
        {
            RelayOnline = false;
        }
        catch (MessengerException e)
        {
            Notice = e.Message;
        }
        finally
        {
            _syncing = false;
        }
    }

    private async Task RefreshStatusesAsync(ContactViewModel open)
    {
        var byId = (await messenger.HistoryAsync(open.Username, 200)).ToDictionary(m => m.Id);
        if (Selected != open) return;
        foreach (var vm in Messages)
            if (byId.TryGetValue(vm.Id, out var stored)) vm.Status = stored.Status;
    }

    private void MoveToTop(ContactViewModel c)
    {
        var i = Contacts.IndexOf(c);
        if (i > 0) Contacts.Move(i, 0);
    }
}

/// <summary>
/// The safety-number panel. Normally it shows the number for the key you trust, with
/// "Mark as verified". After a key change it shows the number for the relay's new key,
/// and trusting it is an explicit choice.
/// </summary>
public sealed partial class SafetyNumberViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly IdentityCheck? _check;

    private SafetyNumberViewModel(ShellViewModel shell, ContactViewModel peer, string number, IdentityCheck? check)
    {
        _shell = shell;
        Peer = peer;
        _check = check;
        var groups = number.Split(' ');
        Rows = [string.Join("  ", groups[..4]), string.Join("  ", groups[4..8]), string.Join("  ", groups[8..])];
    }

    public static SafetyNumberViewModel ForCurrentKey(ShellViewModel shell, ContactViewModel peer, string number) =>
        new(shell, peer, number, null);

    public static SafetyNumberViewModel ForKeyChange(ShellViewModel shell, ContactViewModel peer, IdentityCheck check) =>
        new(shell, peer, check.SafetyNumber, check);

    public ContactViewModel Peer { get; }

    /// <summary>The 60 digits as three rows of four groups.</summary>
    public string[] Rows { get; }

    public bool IsKeyChange => _check is not null;
    public bool IsCurrentKey => !IsKeyChange;
    public bool IsVerified => IsCurrentKey && Peer.Verified;
    public bool IsUnverified => IsCurrentKey && !Peer.Verified;

    public string Explanation => IsKeyChange
        ? $"{Peer.Username}'s identity key changed. This is the safety number for the new key. Compare it with the one " +
          $"{Peer.Username} sees, in person or on a call. If they match, trust the new key. If not, someone may be " +
          "impersonating them: don't trust it."
        : $"Compare these numbers with the ones {Peer.Username} sees on their screen, in person or on a call. If they " +
          "match, your conversation is end-to-end encrypted with no one in the middle.";

    [RelayCommand] private Task MarkVerifiedAsync() => _shell.SetVerifiedAsync(Peer, true);
    [RelayCommand] private Task ClearVerifiedAsync() => _shell.SetVerifiedAsync(Peer, false);
    [RelayCommand] private Task TrustAndVerifyAsync() => _shell.TrustAsync(Peer, _check!, verified: true);
    [RelayCommand] private Task TrustWithoutVerifyingAsync() => _shell.TrustAsync(Peer, _check!, verified: false);
    [RelayCommand] private void Close() => _shell.CloseSafety();
}
