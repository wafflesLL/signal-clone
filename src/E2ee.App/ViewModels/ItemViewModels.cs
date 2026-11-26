using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using E2ee.Messaging;

namespace E2ee.App.ViewModels;

internal static class Display
{
    public static string Time(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("MMM d");
    }
}

/// <summary>One row in the conversation list: a direct chat with a contact, or a group.</summary>
public sealed partial class ContactViewModel : ViewModelBase
{
    private static readonly string[] Palette = ["#3A76F0", "#2E9E6B", "#C2410C", "#8B5CF6", "#DB2777", "#0E7490", "#A16207"];

    public ContactViewModel(Contact contact, StoredMessage? last = null) : this(contact.Username, last)
    {
        Verified = contact.Verified;
        IdentityChanged = contact.IdentityChanged;
        State = contact.State;
    }

    public ContactViewModel(string username, StoredMessage? last = null)
    {
        Username = username;
        Title = username;
        AvatarBrush = BrushFor(username);
        if (last is not null) Update(last);
    }

    public ContactViewModel(Group group, StoredMessage? last = null)
    {
        Username = "";
        GroupId = group.Id;
        Title = group.Name;
        AvatarBrush = BrushFor(group.Id);
        Refresh(group);
        if (last is not null) Update(last);
    }

    private static IBrush BrushFor(string key)
    {
        var hash = key.Aggregate(17, (h, ch) => h * 31 + ch);
        return SolidColorBrush.Parse(Palette[(hash & 0x7fffffff) % Palette.Length]);
    }

    /// <summary>The contact's username. Empty for a group.</summary>
    public string Username { get; }

    /// <summary>Set for a group chat.</summary>
    public string? GroupId { get; }
    public bool IsGroup => GroupId is not null;
    public bool IsDirect => !IsGroup;

    /// <summary>What the list and header show: the username or the group's name.</summary>
    public string Title { get; }
    public string Initial => IsGroup ? "#" : Title[..1].ToUpperInvariant();
    public IBrush AvatarBrush { get; }

    [ObservableProperty] private string _preview = "No messages yet";
    [ObservableProperty] private string _timeText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    private int _unread;

    public bool HasUnread => Unread > 0;

    /// <summary>Set when this contact's identity key no longer matches the remembered one.</summary>
    [ObservableProperty] private bool _identityChanged;

    /// <summary>The user compared safety numbers with this contact.</summary>
    [ObservableProperty] private bool _verified;

    /// <summary>Friends, or a request one way or the other. Groups count as friends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFriend), nameof(IsIncomingRequest), nameof(IsOutgoingRequest))]
    private ContactState _state = ContactState.Friend;

    public bool IsFriend => IsGroup || State == ContactState.Friend;
    public bool IsIncomingRequest => IsDirect && State == ContactState.RequestReceived;
    public bool IsOutgoingRequest => IsDirect && State == ContactState.RequestSent;

    partial void OnStateChanged(ContactState value)
    {
        if (_hasMessages) return;
        Preview = value switch
        {
            ContactState.RequestReceived => "Wants to chat with you",
            ContactState.RequestSent => "Request sent",
            _ => "No messages yet",
        };
    }

    private bool _hasMessages;

    /// <summary>A group's other members.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MembersText))]
    private IReadOnlyList<string> _members = [];

    /// <summary>False once you left the group.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLeft))]
    private bool _active = true;

    public bool HasLeft => IsGroup && !Active;

    public string MembersText => Members.Count == 0 ? "Just you" : "You, " + string.Join(", ", Members);

    public void Refresh(Group group)
    {
        Members = group.Members;
        Active = group.Active;
    }

    public void Update(StoredMessage m)
    {
        _hasMessages = true;
        var who = m.IsEvent ? "" : m.Outgoing ? "You: " : IsGroup ? $"{m.Peer}: " : "";
        var body = m.Text.ReplaceLineEndings(" ");
        if (m.Attachment is { } a) body = $"{a.Icon} " + (body.Length > 0 ? body : a.IsDocument ? a.FileName : a.Kind);
        Preview = who + body;
        TimeText = Display.Time(m.SentAt);
    }
}

/// <summary>What a message bubble can ask the shell to do.</summary>
public interface IMessageActions
{
    string Me { get; }
    Task ReactAsync(MessageViewModel message, string? emoji);
    Task OpenAttachmentAsync(MessageViewModel message);
    Task SaveAttachmentAsync(MessageViewModel message);
}

public sealed partial class MessageViewModel : ViewModelBase
{
    private readonly IMessageActions? _actions;
    private readonly string _time;
    private readonly bool _inGroup;

    public MessageViewModel(StoredMessage m, IMessageActions? actions = null)
    {
        _actions = actions;
        Id = m.Id;
        Text = m.Text;
        Outgoing = m.Outgoing;
        IsEvent = m.IsEvent;
        Sender = m.Peer;
        _inGroup = m.GroupId is not null;
        ShowSender = _inGroup && !m.Outgoing && !m.IsEvent;
        _time = Display.Time(m.SentAt);
        _collapsed = Collapse(m.Text);
        Update(m);
    }

    public long Id { get; }
    public string Text { get; }
    public bool HasText => Text.Length > 0;

    // ---------------------------------------------------------------- long messages

    /// <summary>Messages longer than this many characters or lines are shown shortened, with "Show more".</summary>
    public const int CollapseChars = 700, CollapseLines = 12;

    private readonly string? _collapsed;

    /// <summary>The shortened text, or null if the message is short enough to show whole.</summary>
    private static string? Collapse(string text)
    {
        var lines = text.Split('\n');
        if (text.Length <= CollapseChars && lines.Length <= CollapseLines) return null;
        var cut = string.Join('\n', lines.Take(CollapseLines));
        if (cut.Length > CollapseChars)
        {
            cut = cut[..CollapseChars];
            // End on a word boundary when there is one nearby, and never split a surrogate pair.
            var space = cut.LastIndexOfAny([' ', '\n']);
            if (space > CollapseChars - 80) cut = cut[..space];
            else if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        }
        return cut.TrimEnd() + "…";
    }

    public bool IsLong => _collapsed is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText), nameof(ShowMoreText))]
    private bool _expanded;

    public string DisplayText => _collapsed is null || Expanded ? Text : _collapsed;
    public string ShowMoreText => Expanded ? "Show less" : "Show more";

    [RelayCommand] private void ToggleExpanded() => Expanded = !Expanded;
    public bool Outgoing { get; }

    /// <summary>A line about the group ("bob left"), shown centered instead of as a bubble.</summary>
    public bool IsEvent { get; }
    public bool IsBubble => !IsEvent;

    /// <summary>Who wrote it, shown above incoming group messages.</summary>
    public string Sender { get; }
    public bool ShowSender { get; }

    /// <summary>The sender's color slot in this chat (see <see cref="SenderColors"/>).</summary>
    public int SenderSlot { get; private set; }

    [ObservableProperty] private IBrush? _senderBrush;

    public void SetSenderSlot(int slot)
    {
        SenderSlot = slot;
        SenderBrush = SenderColors.Brush(slot);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText), nameof(IsPending), nameof(IsFailed))]
    private MessageStatus _status;

    public bool IsPending => Status == MessageStatus.Pending;
    public bool IsFailed => Status == MessageStatus.Failed;

    public string MetaText => Status switch
    {
        MessageStatus.Pending => $"{_time} · sending…",
        MessageStatus.Failed => _inGroup ? $"{_time} · not delivered to everyone" : $"{_time} · not delivered",
        _ => _time,
    };

    // ---------------------------------------------------------------- attachment

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachment), nameof(IsDownloading), nameof(AttachmentFailed), nameof(ShowFileCard), nameof(FileLabel), nameof(IsReady))]
    private Attachment? _attachment;

    /// <summary>The decoded image, once loaded. Null for videos and for images this platform can't decode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage), nameof(ShowFileCard))]
    private Avalonia.Media.Imaging.Bitmap? _image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFileCard))]
    private bool _imageLoading;

    public bool HasAttachment => Attachment is not null;
    public bool IsReady => Attachment is { State: AttachmentState.Ready };
    public bool IsDownloading => Attachment is { State: AttachmentState.Downloading };
    public bool AttachmentFailed => Attachment is { State: AttachmentState.Failed };
    public bool HasImage => Image is not null;

    /// <summary>Videos, and images that can't be shown inline: a card with Open and Save.</summary>
    public bool ShowFileCard => IsReady && Image is null && !ImageLoading;

    public string FileLabel => Attachment is { } a ? $"{(a.IsImage ? "🖼" : a.Icon)}  {a.FileName} · {MediaTypes.FormatSize(a.Size)}" : "";

    [RelayCommand] private Task OpenAsync() => _actions?.OpenAttachmentAsync(this) ?? Task.CompletedTask;
    [RelayCommand] private Task SaveAsync() => _actions?.SaveAttachmentAsync(this) ?? Task.CompletedTask;

    // ---------------------------------------------------------------- reactions

    public static IReadOnlyList<string> QuickReactions => Messenger.QuickReactions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReactions), nameof(ReactionsText), nameof(ReactionsTip), nameof(MyReaction))]
    private IReadOnlyList<Reaction> _reactions = [];

    public bool HasReactions => Reactions.Count > 0;

    /// <summary>"👍 2  ❤️" — each emoji once, with a count when more than one person used it.</summary>
    public string ReactionsText => string.Join("  ", Reactions.GroupBy(r => r.Emoji)
        .Select(g => g.Count() > 1 ? $"{g.Key} {g.Count()}" : g.Key));

    public string ReactionsTip => string.Join("\n", Reactions.Select(r => $"{r.Emoji}  {(r.Reactor == _actions?.Me ? "You" : r.Reactor)}"));

    public string? MyReaction => Reactions.FirstOrDefault(r => r.Reactor == _actions?.Me)?.Emoji;

    /// <summary>Picking your current reaction again takes it back.</summary>
    [RelayCommand]
    private Task ReactAsync(string emoji) => _actions?.ReactAsync(this, emoji == MyReaction ? null : emoji) ?? Task.CompletedTask;

    public void Update(StoredMessage m)
    {
        Status = m.Status;
        Attachment = m.Attachment;
        Reactions = m.Reactions;
    }
}
