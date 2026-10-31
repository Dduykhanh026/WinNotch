using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinNotch.Models;

public sealed partial class ShelfItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ShelfItemKind Kind { get; set; } = ShelfItemKind.File;
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SubLabel { get; set; } = string.Empty;
    public string? TextContent { get; set; }
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;

    [ObservableProperty]
    [property: JsonIgnore]
    private ImageSource? _thumbnail;

    [JsonIgnore]
    public bool IsUrl => Kind == ShelfItemKind.TextSnippet &&
                         !string.IsNullOrWhiteSpace(TextContent) &&
                         Uri.TryCreate(TextContent.Trim(), UriKind.Absolute, out Uri? uri) &&
                         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    [JsonIgnore]
    public IconsaxKind FallbackIcon => Kind switch
    {
        ShelfItemKind.Folder => IconsaxKind.Folder,
        ShelfItemKind.ImageClip => IconsaxKind.Gallery,
        ShelfItemKind.TextSnippet => IsUrl ? IconsaxKind.Global : IconsaxKind.NoteText,
        _ => IconsaxKind.DocumentText
    };

    [JsonIgnore]
    public string TooltipText => IsUrl && !string.IsNullOrEmpty(TextContent) ? TextContent : FilePath;
}
