using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
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
        var who = m.IsEvent ? "" : m.Outgoing ? "You: " : IsGroup ? $"{m.Peer}: " : "";
        Preview = who + m.Text.ReplaceLineEndings(" ");
        TimeText = Display.Time(m.SentAt);
    }
}

public sealed partial class MessageViewModel(StoredMessage m) : ViewModelBase
{
    public long Id { get; } = m.Id;
    public string Text { get; } = m.Text;
    public bool Outgoing { get; } = m.Outgoing;

    /// <summary>A line about the group ("bob left"), shown centered instead of as a bubble.</summary>
    public bool IsEvent { get; } = m.IsEvent;
    public bool IsBubble => !IsEvent;

    /// <summary>Who wrote it, shown above incoming group messages.</summary>
    public string Sender { get; } = m.Peer;
    public bool ShowSender { get; } = m.GroupId is not null && !m.Outgoing && !m.IsEvent;

    private readonly string _time = Display.Time(m.SentAt);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText), nameof(IsPending), nameof(IsFailed))]
    private MessageStatus _status = m.Status;

    public bool IsPending => Status == MessageStatus.Pending;
    public bool IsFailed => Status == MessageStatus.Failed;

    public string MetaText => Status switch
    {
        MessageStatus.Pending => $"{_time} · sending…",
        MessageStatus.Failed => m.GroupId is null ? $"{_time} · not delivered" : $"{_time} · not delivered to everyone",
        _ => _time,
    };
}
