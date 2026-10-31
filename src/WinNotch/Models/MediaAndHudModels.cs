using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinNotch.Models;

public sealed class HudPayload
{
    public HudKind Kind { get; init; }
    public IconsaxKind Icon { get; init; } = IconsaxKind.VolumeHigh;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string BodyText { get; init; } = string.Empty;
    public string TimestampBadge { get; init; } = "Bây giờ";
    public string ValueText { get; init; } = string.Empty;
    public double NormalizedValue { get; init; }
    public bool IsInteractiveSlider { get; init; }
    public bool IsMuted { get; init; }
    public string AccentHex { get; init; } = "#FFFFFF";
    public ImageSource? Thumbnail { get; init; }
    public string SourceAumid { get; init; } = string.Empty;
    public IntPtr TargetWindowHandle { get; init; }
}

public sealed class MediaSnapshot
{
    public bool HasSession { get; init; }
    public bool IsPlaying { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string SourceAppId { get; init; } = string.Empty;
    public string SourceDisplayName { get; init; } = string.Empty;
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public BitmapSource? AlbumArt { get; init; }
    public Color DominantColor { get; init; } = Color.FromRgb(82, 130, 255);

    public static MediaSnapshot Empty { get; } = new() { HasSession = false };
}
