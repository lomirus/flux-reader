namespace FluxReader.Core.Services;

public sealed class RequestTimeoutHandler : DelegatingHandler
{
    public const int MaximumTimeoutSeconds = 4_294_967;

    private int _timeoutSeconds;

    public RequestTimeoutHandler(HttpMessageHandler innerHandler, int timeoutSeconds)
        : base(innerHandler)
    {
        TimeoutSeconds = timeoutSeconds;
    }

    public int TimeoutSeconds
    {
        get => Volatile.Read(ref _timeoutSeconds);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaximumTimeoutSeconds);
            Volatile.Write(ref _timeoutSeconds, value);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var timeoutSeconds = TimeoutSeconds;
        if (timeoutSeconds == 0)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        return await base.SendAsync(request, timeoutSource.Token);
    }
}
