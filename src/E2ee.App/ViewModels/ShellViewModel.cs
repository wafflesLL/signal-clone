using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E2ee.Messaging;

namespace E2ee.App.ViewModels;

/// <summary>The main screen: contact list on the left, the open conversation on the right.
/// Every action goes through <see cref="Messenger"/>; nothing here touches crypto.</summary>
public sealed partial class ShellViewModel(Messenger messenger) : ViewModelBase, IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private DispatcherTimer? _timer;
    private bool _syncing;

    public string Me => messenger.Username!;
    public string ServerText => messenger.Server.Authority;

    public ObservableCollection<ContactViewModel> Contacts { get; } = new();
    public ObservableCollection<MessageViewModel> Messages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(TrustNewKeyCommand))]
    private ContactViewModel? _selected;

    public bool HasSelection => Selected is not null;

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

    public async Task StartAsync(bool poll = true)
    {
        foreach (var c in await messenger.ConversationsAsync())
            Contacts.Add(new ContactViewModel(c.Contact.Username, c.Last));
        await SyncNowAsync();
        if (poll)
        {
            _timer = new DispatcherTimer { Interval = PollInterval };
            _timer.Tick += async (_, _) => await SyncNowAsync();
            _timer.Start();
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        messenger.Dispose();
    }

    partial void OnSelectedChanged(ContactViewModel? value) => _ = LoadConversationAsync(value);

    private async Task LoadConversationAsync(ContactViewModel? contact)
    {
        Messages.Clear();
        if (contact is null) return;
        contact.Unread = 0;
        var history = await messenger.HistoryAsync(contact.Username, 200);
        if (Selected != contact) return; // the user clicked elsewhere meanwhile
        Messages.Clear();
        foreach (var m in history) Messages.Add(new MessageViewModel(m));
    }

    private bool CanSend() => Selected is not null && !string.IsNullOrWhiteSpace(Draft);

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
        catch (IdentityChangedException e)
        {
            peer.IdentityChanged = true;
            TrustNewKeyCommand.NotifyCanExecuteChanged();
            Notice = e.Message;
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
            var vm = new ContactViewModel(contact.Username);
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

    private bool CanTrust() => Selected?.IdentityChanged == true;

    [RelayCommand(CanExecute = nameof(CanTrust))]
    private async Task TrustNewKeyAsync()
    {
        var peer = Selected!;
        try
        {
            await messenger.TrustNewIdentityAsync(peer.Username);
            peer.IdentityChanged = false;
            TrustNewKeyCommand.NotifyCanExecuteChanged();
            Notice = $"Now trusting {peer.Username}'s new key. Your next message starts a fresh session.";
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            Notice = e.Message;
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

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
            if (result.Problems.Count > 0) Notice = string.Join("\n", result.Problems);
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
