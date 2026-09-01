using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FluxReader.Core.Services;
using Microsoft.UI.Xaml;

namespace FluxReader.Models;

public sealed partial class Article : ObservableObject
{
    private const int MaximumListPreviewLength = 256;

    public long Id { get; init; }

    public long FeedId { get; init; }

    public required string ExternalId { get; init; }

    public required string FeedTitle { get; init; }

    public FeedNavigationItem? FeedNavigationItem { get; set; }

    public required string Title { get; init; }

    public required string Link { get; init; }

    public required string Author { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public required string Summary { get; init; }

    public required string Content { get; init; }

    public Visibility FeedTitleVisibility { get; set; } = Visibility.Visible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadGlyph))]
    public partial bool IsRead { get; set; }

    public string PublishedDisplay => PublishedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

    public string Byline
    {
        get
        {
            var parts = new[] { Author, FeedTitle, PublishedDisplay }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            return string.Join("  ·  ", parts);
        }
    }

    public string DisplayContent => string.IsNullOrWhiteSpace(Content) ? Summary : Content;

    public Uri? ContentBaseUri =>
        Uri.TryCreate(Link, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    public string ListPreview
        => ArticleContentParser.CreatePreviewText(
            Summary,
            Content,
            ContentBaseUri,
            MaximumListPreviewLength);

    public string UnreadGlyph => IsRead ? string.Empty : "●";

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(PublishedDisplay));
        OnPropertyChanged(nameof(Byline));
    }

}
