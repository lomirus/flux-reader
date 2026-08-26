using CommunityToolkit.Mvvm.ComponentModel;

namespace FluxReader.Models;

public sealed partial class Feed : ObservableObject
{
    public long Id { get; init; }

    public required string Url { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadDisplay))]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SiteUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? LastRefreshedAt { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadDisplay))]
    public partial int UnreadCount { get; set; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModifiedAt { get; init; }

    public string UnreadDisplay => UnreadCount == 0 ? string.Empty : UnreadCount.ToString();
}
