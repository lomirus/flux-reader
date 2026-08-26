namespace FluxReader.Core.Models;

public sealed record SubscriptionOutline(
    string Title,
    Uri FeedUri,
    Uri? SiteUri = null,
    string? Group = null);

public sealed record SubscriptionDocument(
    IReadOnlyList<SubscriptionOutline> Subscriptions,
    int SkippedOutlineCount);
