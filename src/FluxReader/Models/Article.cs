using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FluxReader.Models;

public sealed partial class Article : ObservableObject
{
    public long Id { get; init; }

    public long FeedId { get; init; }

    public required string ExternalId { get; init; }

    public required string FeedTitle { get; init; }

    public required string Title { get; init; }

    public required string Link { get; init; }

    public required string Author { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public required string Summary { get; init; }

    public required string Content { get; init; }

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

    public string UnreadGlyph => IsRead ? string.Empty : "●";

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(PublishedDisplay));
        OnPropertyChanged(nameof(Byline));
    }
}
