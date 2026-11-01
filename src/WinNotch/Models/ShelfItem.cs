using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinNotch.Models;

public sealed partial class ShelfItem : ObservableObject
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico", ".tiff"
    };

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

    partial void OnThumbnailChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasImagePreview));
        OnPropertyChanged(nameof(HasTextSnippetPreview));
    }

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
    public string KindBadgeText => Kind switch
    {
        ShelfItemKind.ImageClip => "Ảnh chụp",
        ShelfItemKind.Folder => "Thư mục",
        ShelfItemKind.TextSnippet => IsUrl ? "Liên kết URL" : "Văn bản",
        _ => "Tệp tin"
    };

    [JsonIgnore]
    public bool HasImagePreview => Thumbnail != null && (Kind == ShelfItemKind.ImageClip || IsImageFileExtension());

    [JsonIgnore]
    public string PreviewSnippetText
    {
        get
        {
            if (Kind != ShelfItemKind.TextSnippet || string.IsNullOrWhiteSpace(TextContent))
                return string.Empty;

            string trimmed = TextContent.Trim();
            return trimmed.Length > 240 ? trimmed[..240].TrimEnd() + "…" : trimmed;
        }
    }

    [JsonIgnore]
    public bool HasTextSnippetPreview => !HasImagePreview && !string.IsNullOrWhiteSpace(PreviewSnippetText);

    [JsonIgnore]
    public bool HasFilePathLine => !string.IsNullOrWhiteSpace(FilePath) && Kind is ShelfItemKind.File or ShelfItemKind.Folder;

    [JsonIgnore]
    public string TooltipText => IsUrl && !string.IsNullOrEmpty(TextContent) ? TextContent : FilePath;

    private bool IsImageFileExtension()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            return false;

        try
        {
            string ext = Path.GetExtension(FilePath);
            return !string.IsNullOrEmpty(ext) && ImageExtensions.Contains(ext);
        }
        catch
        {
            return false;
        }
    }
}
