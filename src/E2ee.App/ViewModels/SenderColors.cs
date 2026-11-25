using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace E2ee.App.ViewModels;

/// <summary>
/// Colors for senders' names in group chats. Each palette has at least 5:1 contrast (WCAG AA
/// asks 4.5:1 for small text) against the incoming bubble and the window background of its
/// theme; AppTests checks this. Slots are ordered so neighbors differ most in hue, and a chat
/// hands them out in order, so a small group always gets clearly different colors.
/// </summary>
public static class SenderColors
{
    // blue, red, green, purple, orange, teal, pink, ochre, indigo, brown, cyan, olive
    public static readonly string[] Light =
        ["#1558B0", "#B71C1C", "#276B2B", "#7B1FA2", "#A33F00", "#00695C", "#AD1457", "#755A00", "#3949AB", "#6D4C41", "#00616B", "#4F5A00"];

    public static readonly string[] Dark =
        ["#64B5F6", "#FF9A90", "#81C784", "#D7A6E0", "#FFAB66", "#5CC9BE", "#F48FB1", "#FFD54F", "#AEB6E6", "#C8B8B2", "#4DD0E1", "#C5E17A"];

    public static int Count => Light.Length;

    private static readonly IBrush[] LightBrushes = Light.Select(c => (IBrush)SolidColorBrush.Parse(c)).ToArray();
    private static readonly IBrush[] DarkBrushes = Dark.Select(c => (IBrush)SolidColorBrush.Parse(c)).ToArray();

    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static IBrush Brush(int slot) => (IsDark ? DarkBrushes : LightBrushes)[slot % Count];
}
