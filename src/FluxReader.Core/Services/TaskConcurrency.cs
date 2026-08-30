namespace FluxReader.Core.Services;

public static class TaskConcurrency
{
    public static async Task<TResult[]> WhenAllAsync<TSource, TResult>(
        IEnumerable<TSource> source,
        int maximumConcurrency,
        Func<TSource, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumConcurrency);

        using var gate = maximumConcurrency > 0
            ? new SemaphoreSlim(maximumConcurrency)
            : null;
        return await Task.WhenAll(source.Select(async item =>
        {
            if (gate is null)
            {
                return await operation(item, cancellationToken);
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                return await operation(item, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
