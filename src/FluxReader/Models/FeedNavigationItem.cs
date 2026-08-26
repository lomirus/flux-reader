using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FluxReader.Models;

public sealed partial class FeedNavigationItem : ObservableObject
{
    private FeedNavigationItem(Feed feed, ActionLabels labels)
    {
        Feed = feed;
        PrimaryActionText = labels.ChangeGroup;
        RemoveActionText = labels.RemoveFeed;
        feed.PropertyChanged += Feed_PropertyChanged;
    }

    private FeedNavigationItem(FeedGroup group, IEnumerable<Feed> feeds, ActionLabels labels)
    {
        Group = group;
        PrimaryActionText = labels.RenameGroup;
        RemoveActionText = labels.RemoveGroup;
        foreach (var feed in feeds)
        {
            var child = new FeedNavigationItem(feed, labels);
            child.PropertyChanged += Child_PropertyChanged;
            Children.Add(child);
        }
    }

    public Feed? Feed { get; }

    public FeedGroup? Group { get; }

    public ObservableCollection<FeedNavigationItem> Children { get; } = [];

    public string PrimaryActionText { get; }

    public string RemoveActionText { get; }

    public bool IsGroup => Group is not null;

    public string Title => Feed?.Title ?? Group?.Name ?? string.Empty;

    public string Glyph => IsGroup ? "\uE8B7" : "\uE789";

    public string PrimaryActionGlyph => IsGroup ? "\uE70F" : "\uE8B7";

    public int UnreadCount => Feed?.UnreadCount ?? Children.Sum(child => child.UnreadCount);

    public string UnreadDisplay => UnreadCount == 0 ? string.Empty : UnreadCount.ToString();

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public static FeedNavigationItem ForFeed(Feed feed, ActionLabels labels) => new(feed, labels);

    public static FeedNavigationItem ForGroup(
        FeedGroup group,
        IEnumerable<Feed> feeds,
        ActionLabels labels) => new(group, feeds, labels);

    public sealed record ActionLabels(
        string ChangeGroup,
        string RemoveFeed,
        string RenameGroup,
        string RemoveGroup);

    private void Feed_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FluxReader.Models.Feed.Title))
        {
            OnPropertyChanged(nameof(Title));
        }

        if (e.PropertyName == nameof(FluxReader.Models.Feed.UnreadCount) ||
            e.PropertyName == nameof(FluxReader.Models.Feed.UnreadDisplay))
        {
            OnPropertyChanged(nameof(UnreadCount));
            OnPropertyChanged(nameof(UnreadDisplay));
        }
    }

    private void Child_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UnreadCount) || e.PropertyName == nameof(UnreadDisplay))
        {
            OnPropertyChanged(nameof(UnreadCount));
            OnPropertyChanged(nameof(UnreadDisplay));
        }
    }
}
