// Attachments and reactions: the types the app sees, and the pointer that travels inside
// an end-to-end encrypted message.

namespace E2ee.Messaging;

public enum AttachmentState { Downloading, Ready, Failed }

/// <summary>An image, video or document attached to a message. The file itself lives on this device.</summary>
public sealed record Attachment(string FileName, string ContentType, long Size, AttachmentState State)
{
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.Ordinal);
    public bool IsVideo => ContentType.StartsWith("video/", StringComparison.Ordinal);
    public bool IsDocument => !IsImage && !IsVideo;

    /// <summary>"Photo", "Video" or "Document", for previews.</summary>
    public string Kind => IsImage ? "Photo" : IsVideo ? "Video" : "Document";

    public string Icon => IsImage ? "📷" : IsVideo ? "🎬" : "📄";
}

/// <summary>One person's reaction to a message. Each person has at most one per message.</summary>
public sealed record Reaction(string Reactor, string Emoji);

/// <summary>
/// What the recipient needs to fetch and decrypt an attachment: the relay's id for the
/// ciphertext, the random AES-256-GCM key, and the SHA-256 of the ciphertext (so a relay that
/// swaps the blob is caught even before decryption). Sent only inside the ratchet.
/// </summary>
internal sealed record AttachmentPointer(string Id, byte[] Key, byte[] Digest, long Size, string ContentType, string FileName);

/// <summary>
/// Only images, videos and documents on this list are accepted, and a file's type always comes
/// from this list (by extension), never from the sender. Left out on purpose: formats that can
/// carry code or macros when opened (.doc/.xls/.ppt, .docm and friends, .html, .svg, .zip,
/// executables and scripts), since "Open" hands the file to another app.
/// </summary>
public static class MediaTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".heic"] = "image/heic", [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4", [".m4v"] = "video/x-m4v", [".mov"] = "video/quicktime", [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska", [".avi"] = "video/x-msvideo",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".md"] = "text/markdown", [".csv"] = "text/csv",
        [".rtf"] = "application/rtf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
    };

    /// <summary>For error messages: what can be sent.</summary>
    public const string Accepted = "photos (jpg, png, gif, webp, heic), videos (mp4, mov, webm) and documents (pdf, txt, md, csv, rtf, docx, xlsx, pptx, odt, ods, odp)";

    private static readonly Dictionary<string, string> ExtensionByType =
        ByExtension.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    public static IReadOnlyCollection<string> Extensions => ByExtension.Keys;

    /// <summary>The content type for a file name, or null if it isn't a kind of file we accept.</summary>
    public static string? ForFileName(string fileName) => ByExtension.GetValueOrDefault(Path.GetExtension(fileName));

    /// <summary>A safe extension for writing the file out (e.g. to open it), chosen from the content type.</summary>
    public static string ExtensionFor(string contentType) => ExtensionByType.GetValueOrDefault(contentType, ".bin");

    /// <summary>A display name with no path, control characters, or excessive length.</summary>
    public static string CleanFileName(string name)
    {
        var clean = new string(Path.GetFileName(name.Replace('\\', '/')).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 100) clean = clean[..80] + "…" + clean[^19..];
        return clean.Length == 0 ? "file" : clean;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };
}
