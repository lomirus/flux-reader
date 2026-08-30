using FluxReader.Core.Services;

namespace FluxReader.Core.Tests;

[TestClass]
public sealed class RequestTimeoutHandlerTests
{
    [TestMethod]
    public async Task SendAsync_ZeroDisablesTimeout_AndPositiveValueCancels()
    {
        using var noTimeoutInvoker = new HttpMessageInvoker(
            new RequestTimeoutHandler(new DelayHandler(TimeSpan.FromMilliseconds(10)), 0));
        using var completedRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        using var completedResponse = await noTimeoutInvoker.SendAsync(completedRequest, CancellationToken.None);

        using var timeoutInvoker = new HttpMessageInvoker(
            new RequestTimeoutHandler(new DelayHandler(Timeout.InfiniteTimeSpan), 1));
        using var timedOutRequest = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => timeoutInvoker.SendAsync(timedOutRequest, CancellationToken.None));
    }

    private sealed class DelayHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
