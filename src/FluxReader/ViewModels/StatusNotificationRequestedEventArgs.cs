namespace FluxReader.ViewModels;

public enum StatusNotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error
}

public sealed class StatusNotificationRequestedEventArgs(
    string message,
    StatusNotificationSeverity severity,
    string? title = null,
    string? actionText = null,
    IReadOnlyList<StatusNotificationDetail>? details = null) : EventArgs
{
    public string Message { get; } = message;

    public StatusNotificationSeverity Severity { get; } = severity;

    public string? Title { get; } = title;

    public string? ActionText { get; } = actionText;

    public IReadOnlyList<StatusNotificationDetail> Details { get; } = details ?? [];
}

public sealed record StatusNotificationDetail(string Title, string Description);
