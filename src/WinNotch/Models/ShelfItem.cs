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
    public IconsaxKind FallbackIcon => Kind switch
    {
        ShelfItemKind.Folder => IconsaxKind.Folder,
        ShelfItemKind.ImageClip => IconsaxKind.Gallery,
        ShelfItemKind.TextSnippet => IconsaxKind.NoteText,
        _ => IconsaxKind.DocumentText
    };
}
