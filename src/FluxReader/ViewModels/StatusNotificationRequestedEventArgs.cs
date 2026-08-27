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
    StatusNotificationSeverity severity) : EventArgs
{
    public string Message { get; } = message;

    public StatusNotificationSeverity Severity { get; } = severity;
}
