using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class TaskConcurrencyTests
{
    [TestMethod]
    [DataRow(3, 3)]
    [DataRow(0, 6)]
    public async Task WhenAllAsync_UsesConfiguredMaximumConcurrency(
        int maximumConcurrency,
        int expectedObservedConcurrency)
    {
        var activeCount = 0;
        var maximumActiveCount = 0;
        var sync = new object();

        var results = await TaskConcurrency.WhenAllAsync(
            Enumerable.Range(0, 6),
            maximumConcurrency,
            async (value, cancellationToken) =>
            {
                var currentActiveCount = Interlocked.Increment(ref activeCount);
                lock (sync)
                {
                    maximumActiveCount = Math.Max(maximumActiveCount, currentActiveCount);
                }

                await Task.Delay(10, cancellationToken);
                Interlocked.Decrement(ref activeCount);
                return value;
            });

        CollectionAssert.AreEqual(Enumerable.Range(0, 6).ToArray(), results);
        Assert.AreEqual(expectedObservedConcurrency, maximumActiveCount);
    }
}
