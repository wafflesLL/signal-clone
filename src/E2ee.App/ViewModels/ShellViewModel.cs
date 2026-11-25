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
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanCompose), nameof(ShowSafetyButton), nameof(CanManageGroup))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(ShowSafetyNumberCommand), nameof(OpenAddMembersCommand), nameof(LeaveGroupCommand))]
    private ContactViewModel? _selected;

    public bool HasSelection => Selected is not null;

    /// <summary>False while a direct chat's key change is unresolved, or after leaving a group.</summary>
    public bool CanCompose => Selected switch
    {
        { IsGroup: true } g => g.Active,
        { } c => !c.IdentityChanged,
        null => false,
    };

    public bool ShowSafetyButton => Selected is { IsGroup: false, IdentityChanged: false };

    public bool CanManageGroup => Selected is { IsGroup: true, Active: true };

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
        if (Avalonia.Application.Current is { } app) app.ActualThemeVariantChanged += OnThemeChanged;
        foreach (var c in await messenger.ConversationsAsync())
            Contacts.Add(c.Group is { } g ? new ContactViewModel(g, c.Last) : new ContactViewModel(c.Contact!, c.Last));
        await SyncNowAsync();
        if (push) _ = PushLoopAsync(_stop.Token);
    }

    public void Dispose()
    {
        if (Avalonia.Application.Current is { } app) app.ActualThemeVariantChanged -= OnThemeChanged;
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
        var history = contact.IsGroup
            ? await messenger.GroupHistoryAsync(contact.GroupId!, 200)
            : await messenger.HistoryAsync(contact.Username, 200);
        if (Selected != contact) return; // the user clicked elsewhere meanwhile
        Messages.Clear();
        _senderSlots.Clear();
        // Everyone in the chat gets a color up front, in name order, so colors don't depend on who spoke first.
        if (contact.IsGroup)
            foreach (var person in contact.Members.Concat(history.Where(m => !m.Outgoing && !m.IsEvent).Select(m => m.Peer)).Distinct().Order())
                SlotFor(person);
        foreach (var m in history) AddMessage(m);
    }

    /// <summary>Color slots of the people in the open chat. Nobody's color changes while it stays open.</summary>
    private readonly Dictionary<string, int> _senderSlots = new();

    private int SlotFor(string person)
    {
        if (!_senderSlots.TryGetValue(person, out var slot))
            _senderSlots[person] = slot = _senderSlots.Count % SenderColors.Count;
        return slot;
    }

    private void AddMessage(StoredMessage m)
    {
        var vm = new MessageViewModel(m);
        if (vm.ShowSender) vm.SetSenderSlot(SlotFor(m.Peer));
        Messages.Add(vm);
    }

    /// <summary>Light and dark mode use different palettes.</summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        foreach (var m in Messages.Where(m => m.ShowSender)) m.SetSenderSlot(m.SenderSlot);
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
            var msg = peer.IsGroup ? await messenger.SendGroupAsync(peer.GroupId!, text) : await messenger.SendAsync(peer.Username, text);
            if (Selected == peer) AddMessage(msg);
            peer.Update(msg);
            MoveToTop(peer);
            RelayOnline = msg.Status != MessageStatus.Pending;
        }
        catch (IdentityChangedException e)
        {
            MarkIdentityChanged(e.Username);
            if (peer.IsGroup)
                Notice = $"{e.Username}'s safety number changed, so nothing was sent to this group. Open your chat with " +
                         $"{e.Username} and review it first.";
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
        if (Contacts.FirstOrDefault(c => c.IsDirect && c.Username == name) is { } existing)
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

    private bool CanShowSafetyNumber() => Selected is { IsGroup: false };

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
            OnPropertyChanged(nameof(ShowSafetyButton));
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
        if (Contacts.FirstOrDefault(c => c.IsDirect && c.Username == username) is not { } c) return;
        c.IdentityChanged = true;
        c.Verified = false;
        if (c == Selected)
        {
            OnPropertyChanged(nameof(CanCompose));
            OnPropertyChanged(nameof(ShowSafetyButton));
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    // ---------------------------------------------------------------- groups

    /// <summary>The new-group / add-people panel, when open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupFormOpen))]
    private GroupFormViewModel? _groupForm;

    public bool IsGroupFormOpen => GroupForm is not null;

    [RelayCommand]
    private void OpenNewGroup() => GroupForm = new GroupFormViewModel(this, null);

    [RelayCommand(CanExecute = nameof(CanManageGroup))]
    private void OpenAddMembers() => GroupForm = new GroupFormViewModel(this, Selected);

    internal void CloseGroupForm() => GroupForm = null;

    internal async Task CreateGroupAsync(string name, IReadOnlyList<string> members)
    {
        try
        {
            var group = await messenger.CreateGroupAsync(name, members);
            var vm = new ContactViewModel(group, (await messenger.GroupHistoryAsync(group.Id, 1)).LastOrDefault());
            Contacts.Insert(0, vm);
            GroupForm = null;
            Selected = vm;
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            GroupForm?.SetError(GroupError(e));
        }
    }

    internal async Task AddMembersAsync(ContactViewModel group, IReadOnlyList<string> members)
    {
        try
        {
            group.Refresh(await messenger.AddGroupMembersAsync(group.GroupId!, members));
            GroupForm = null;
            await ShowLatestAsync(group);
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            GroupForm?.SetError(GroupError(e));
        }
    }

    private string GroupError(Exception e)
    {
        if (e is RelayException { Status: null })
        {
            RelayOnline = false;
            return "Can't reach the relay. Try again when it's back.";
        }
        if (e is IdentityChangedException ic)
        {
            MarkIdentityChanged(ic.Username);
            return $"{ic.Username}'s safety number changed. Review it in your chat with {ic.Username} first.";
        }
        return e.Message;
    }

    [RelayCommand(CanExecute = nameof(CanManageGroup))]
    private async Task LeaveGroupAsync()
    {
        var group = Selected!;
        try
        {
            group.Refresh(await messenger.LeaveGroupAsync(group.GroupId!));
            GroupStateChanged(group);
            await ShowLatestAsync(group);
        }
        catch (Exception e) when (e is MessengerException or RelayException)
        {
            Notice = e is RelayException { Status: null } ? "Can't reach the relay to tell the group. Try again when it's back." : e.Message;
        }
    }

    /// <summary>Adds the group's newest line (an event we just wrote) to the list and the open chat.</summary>
    private async Task ShowLatestAsync(ContactViewModel group)
    {
        if ((await messenger.GroupHistoryAsync(group.GroupId!, 1)).LastOrDefault() is not { } last) return;
        group.Update(last);
        MoveToTop(group);
        if (Selected == group && Messages.All(m => m.Id != last.Id)) AddMessage(last);
    }

    private void GroupStateChanged(ContactViewModel group)
    {
        if (group != Selected) return;
        OnPropertyChanged(nameof(CanCompose));
        OnPropertyChanged(nameof(CanManageGroup));
        SendCommand.NotifyCanExecuteChanged();
        OpenAddMembersCommand.NotifyCanExecuteChanged();
        LeaveGroupCommand.NotifyCanExecuteChanged();
    }

    private ContactViewModel ConversationFor(StoredMessage msg)
    {
        if (msg.GroupId is { } id)
        {
            var group = Contacts.FirstOrDefault(c => c.GroupId == id);
            if (msg.IsEvent || group is null)
            {
                var stored = messenger.GetGroup(id)!;
                if (group is null) Contacts.Insert(0, group = new ContactViewModel(stored));
                else group.Refresh(stored);
                GroupStateChanged(group);
            }
            return group;
        }
        var contact = Contacts.FirstOrDefault(c => c.IsDirect && c.Username == msg.Peer);
        if (contact is null) Contacts.Insert(0, contact = new ContactViewModel(msg.Peer));
        return contact;
    }

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
                var contact = ConversationFor(msg);
                contact.Update(msg);
                MoveToTop(contact);
                if (Selected == contact) AddMessage(msg);
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
        var history = open.IsGroup ? await messenger.GroupHistoryAsync(open.GroupId!, 200) : await messenger.HistoryAsync(open.Username, 200);
        var byId = history.ToDictionary(m => m.Id);
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

/// <summary>The panel for creating a group, or for adding people to the open one.</summary>
public sealed partial class GroupFormViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly ContactViewModel? _group;

    internal GroupFormViewModel(ShellViewModel shell, ContactViewModel? addTo)
    {
        _shell = shell;
        _group = addTo;
    }

    public bool IsNew => _group is null;
    public string Heading => IsNew ? "New group" : $"Add people to {_group!.Title}";
    public string SubmitText => IsNew ? "Create group" : "Add";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _name = "";

    /// <summary>Usernames separated by commas or spaces.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _members = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    internal void SetError(string message) => Error = message;

    private List<string> MemberList() =>
        Members.Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private bool CanSubmit() => MemberList().Count > 0 && (!IsNew || !string.IsNullOrWhiteSpace(Name));

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task SubmitAsync()
    {
        Error = null;
        return IsNew ? _shell.CreateGroupAsync(Name, MemberList()) : _shell.AddMembersAsync(_group!, MemberList());
    }

    [RelayCommand] private void Close() => _shell.CloseGroupForm();
}
