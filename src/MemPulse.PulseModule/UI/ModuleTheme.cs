using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MemPulse.PulseModule.UI;

internal static class ModuleTheme
{
    public static readonly IBrush SurfaceBackground = Brush.Parse("#0B1118");
    public static readonly IBrush PanelBackground = Brush.Parse("#101822");
    public static readonly IBrush CardBackground = Brush.Parse("#151F2C");
    public static readonly IBrush CardAltBackground = Brush.Parse("#1B2736");
    public static readonly IBrush BorderBrush = Brush.Parse("#253549");
    public static readonly IBrush BorderSubtleBrush = Brush.Parse("#1D2B3C");
    public static readonly IBrush AccentBrush = Brush.Parse("#38BDF8");
    public static readonly IBrush AccentSoftBrush = Brush.Parse("#16324A");
    public static readonly IBrush SuccessBrush = Brush.Parse("#34D399");
    public static readonly IBrush WarningBrush = Brush.Parse("#FBBF24");
    public static readonly IBrush DangerBrush = Brush.Parse("#F87171");
    public static readonly IBrush TextPrimary = Brush.Parse("#F1F5F9");
    public static readonly IBrush TextSecondary = Brush.Parse("#CBD5E1");
    public static readonly IBrush TextMuted = Brush.Parse("#94A3B8");
    public static readonly IBrush TrackBrush = Brush.Parse("#1E2C3E");

    public static IBrush SeverityBrush(double usedPercent) =>
        usedPercent switch
        {
            >= 88 => DangerBrush,
            >= 72 => WarningBrush,
            _ => AccentBrush
        };

    public static Color SeverityColor(double usedPercent) =>
        usedPercent switch
        {
            >= 88 => Color.Parse("#F87171"),
            >= 72 => Color.Parse("#FBBF24"),
            _ => Color.Parse("#38BDF8")
        };

    public static Border CreateCard(Control child, Thickness? padding = null, IBrush? background = null) =>
        new()
        {
            Background = background ?? CardBackground,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = padding ?? new Thickness(16),
            Child = child
        };

    public static Button CreatePrimaryButton(string text) =>
        new()
        {
            Content = text,
            Background = AccentBrush,
            Foreground = Brush.Parse("#08111B"),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(16, 9),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left
        };

    public static Button CreateSecondaryButton(string text) =>
        new()
        {
            Content = text,
            Background = CardAltBackground,
            Foreground = TextPrimary,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left
        };

    public static Button CreateCompactButton(string text) =>
        new()
        {
            Content = text,
            Background = CardAltBackground,
            Foreground = TextSecondary,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(7),
            FontSize = 12
        };

    public static TextBlock CreateSectionTitle(string text) =>
        new()
        {
            Text = text,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextPrimary
        };

    public static TextBlock CreateMutedLabel(string text, double fontSize = 12) =>
        new()
        {
            Text = text,
            FontSize = fontSize,
            Foreground = TextMuted,
            TextWrapping = TextWrapping.Wrap
        };
}
