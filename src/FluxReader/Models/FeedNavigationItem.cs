using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace FluxReader.Models;

public sealed partial class FeedNavigationItem : ObservableObject
{
    private const double ChildIndentWidth = 18;
    private readonly string _refreshFailedText;
    private ImageSource? _iconSource;
    private long _iconLoadVersion;

    private FeedNavigationItem(Feed feed, ActionLabels labels, bool isChild)
    {
        Feed = feed;
        IsChild = isChild;
        RefreshActionText = labels.RefreshFeed;
        EditActionText = labels.EditFeed;
        PrimaryActionText = labels.ChangeGroup;
        RemoveActionText = labels.RemoveFeed;
        _refreshFailedText = labels.RefreshFailed;
        feed.PropertyChanged += Feed_PropertyChanged;
        _ = LoadIconSourceAsync(feed.IconUrl);
    }

    private FeedNavigationItem(FeedGroup group, IEnumerable<Feed> feeds, ActionLabels labels)
    {
        Group = group;
        RefreshActionText = labels.RefreshFeed;
        EditActionText = labels.EditFeed;
        PrimaryActionText = labels.RenameGroup;
        RemoveActionText = labels.RemoveGroup;
        _refreshFailedText = labels.RefreshFailed;
        foreach (var feed in feeds)
        {
            var child = new FeedNavigationItem(feed, labels, isChild: true);
            child.PropertyChanged += Child_PropertyChanged;
            Children.Add(child);
        }
    }

    public Feed? Feed { get; }

    public FeedGroup? Group { get; }

    public ObservableCollection<FeedNavigationItem> Children { get; } = [];

    public string RefreshActionText { get; }

    public string EditActionText { get; }

    public string PrimaryActionText { get; }

    public string RemoveActionText { get; }

    public bool IsGroup => Group is not null;

    public bool IsChild { get; }

    public GridLength NavigationIndent => new(IsChild ? ChildIndentWidth : 0);

    public Visibility ChevronVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FeedActionVisibility => Feed is not null
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public string Title => Feed?.Title ?? Group?.Name ?? string.Empty;

    public string Glyph => IsGroup ? "\uE8B7" : "\uE789";

    public ImageSource? IconSource
    {
        get => _iconSource;
        private set => SetProperty(ref _iconSource, value);
    }

    public string PrimaryActionGlyph => IsGroup ? "\uE70F" : "\uE8B7";

    public int UnreadCount => Feed?.UnreadCount ?? Children.Sum(child => child.UnreadCount);

    public string UnreadDisplay => UnreadCount == 0 ? string.Empty : UnreadCount.ToString();

    public Visibility RefreshWarningVisibility => string.IsNullOrWhiteSpace(Feed?.LastRefreshError)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string RefreshWarningToolTip => string.IsNullOrWhiteSpace(Feed?.LastRefreshError)
        ? string.Empty
        : $"{_refreshFailedText}{Environment.NewLine}{Feed.LastRefreshError}";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial Visibility IconFallbackVisibility { get; set; } = Visibility.Visible;

    public static FeedNavigationItem ForFeed(Feed feed, ActionLabels labels) =>
        new(feed, labels, isChild: false);

    public static FeedNavigationItem ForGroup(
        FeedGroup group,
        IEnumerable<Feed> feeds,
        ActionLabels labels) => new(group, feeds, labels);

    public sealed record ActionLabels(
        string RefreshFeed,
        string EditFeed,
        string ChangeGroup,
        string RemoveFeed,
        string RenameGroup,
        string RemoveGroup,
        string RefreshFailed);

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ChevronGlyph));

    private void Feed_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FluxReader.Models.Feed.Title))
        {
            OnPropertyChanged(nameof(Title));
        }

        if (e.PropertyName == nameof(FluxReader.Models.Feed.IconUrl))
        {
            IconFallbackVisibility = Visibility.Visible;
            IconSource = null;
            _ = LoadIconSourceAsync(Feed?.IconUrl);
        }

        if (e.PropertyName == nameof(FluxReader.Models.Feed.UnreadCount) ||
            e.PropertyName == nameof(FluxReader.Models.Feed.UnreadDisplay))
        {
            OnPropertyChanged(nameof(UnreadCount));
            OnPropertyChanged(nameof(UnreadDisplay));
        }

        if (e.PropertyName == nameof(FluxReader.Models.Feed.LastRefreshError))
        {
            OnPropertyChanged(nameof(RefreshWarningVisibility));
            OnPropertyChanged(nameof(RefreshWarningToolTip));
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

    private async Task LoadIconSourceAsync(string? value)
    {
        var version = Interlocked.Increment(ref _iconLoadVersion);
        var isLocalFile = Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile;
        var source = await CreateIconSourceAsync(value);
        if (version == Volatile.Read(ref _iconLoadVersion))
        {
            IconSource = source;
            if (isLocalFile)
            {
                IconFallbackVisibility = source is null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }

    private static async Task<ImageSource?> CreateIconSourceAsync(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        {
            return CreateUriIconSource(uri);
        }

        if (!uri.IsFile || !File.Exists(uri.LocalPath))
        {
            return null;
        }

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(uri.LocalPath);
            using var stream = await file.OpenReadAsync();
            if (Path.GetExtension(uri.LocalPath).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var svgSource = new SvgImageSource();
                var status = await svgSource.SetSourceAsync(stream);
                return status == SvgImageSourceLoadStatus.Success ? svgSource : null;
            }

            var bitmapSource = new BitmapImage();
            await bitmapSource.SetSourceAsync(stream);
            return bitmapSource;
        }
        catch (Exception exception) when (exception is
                                           ArgumentException or
                                           FileNotFoundException or
                                           IOException or
                                           UnauthorizedAccessException or
                                           System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static ImageSource CreateUriIconSource(Uri uri) =>
        Path.GetExtension(uri.AbsolutePath).Equals(".svg", StringComparison.OrdinalIgnoreCase)
            ? new SvgImageSource { UriSource = uri }
            : new BitmapImage { UriSource = uri };
}
