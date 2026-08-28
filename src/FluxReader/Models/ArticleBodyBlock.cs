using CommunityToolkit.Mvvm.ComponentModel;
using FluxReader.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluxReader.Models;

public sealed partial class ArticleBodyBlock : ObservableObject
{
    private ArticleBodyBlock(ArticleContentBlock source)
    {
        Kind = source.Kind;
        Text = source.Text;
        ImageUri = source.ImageUri;
        ImageSource = CreateImageSource(source.ImageUri);
    }

    public ArticleContentBlockKind Kind { get; }

    public string Text { get; }

    public Uri? ImageUri { get; }

    public ImageSource? ImageSource { get; }

    public string AccessibleName => string.IsNullOrWhiteSpace(Text)
        ? ImageUri?.AbsoluteUri ?? string.Empty
        : Text;

    public string FallbackText => AccessibleName;

    public Visibility ImageVisibility => HasImageFailed ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FallbackVisibility => HasImageFailed ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageVisibility))]
    [NotifyPropertyChangedFor(nameof(FallbackVisibility))]
    public partial bool HasImageFailed { get; set; }

    public static ArticleBodyBlock From(ArticleContentBlock source) => new(source);

    private static ImageSource? CreateImageSource(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        return uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? new SvgImageSource { UriSource = uri }
            : new BitmapImage { UriSource = uri };
    }
}
