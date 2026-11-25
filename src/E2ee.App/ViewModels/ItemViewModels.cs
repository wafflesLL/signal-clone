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
        var hash = username.Aggregate(17, (h, ch) => h * 31 + ch);
        AvatarBrush = SolidColorBrush.Parse(Palette[(hash & 0x7fffffff) % Palette.Length]);
        if (last is not null) Update(last);
    }

    public string Username { get; }
    public string Initial => Username[..1].ToUpperInvariant();
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

    public void Update(StoredMessage m)
    {
        Preview = (m.Outgoing ? "You: " : "") + m.Text.ReplaceLineEndings(" ");
        TimeText = Display.Time(m.SentAt);
    }
}

public sealed partial class MessageViewModel(StoredMessage m) : ViewModelBase
{
    public long Id { get; } = m.Id;
    public string Text { get; } = m.Text;
    public bool Outgoing { get; } = m.Outgoing;
    private readonly string _time = Display.Time(m.SentAt);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText), nameof(IsPending), nameof(IsFailed))]
    private MessageStatus _status = m.Status;

    public bool IsPending => Status == MessageStatus.Pending;
    public bool IsFailed => Status == MessageStatus.Failed;

    public string MetaText => Status switch
    {
        MessageStatus.Pending => $"{_time} · sending…",
        MessageStatus.Failed => $"{_time} · not delivered",
        _ => _time,
    };
}
