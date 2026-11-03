using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AiTranslate.Ai;
using Xunit;

namespace AiTranslate.Tests;

public sealed class AiTransportOverloadTests
{
    [Theory]
    [InlineData(0, HttpStatusCode.OK, 2)]
    [InlineData(0, HttpStatusCode.ServiceUnavailable, 2)]
    [InlineData(60, HttpStatusCode.OK, 1)]
    public async Task RetriesBriefOverloadOnceBeforeStreaming(int retryAfter, HttpStatusCode nextStatus, int expectedRequests)
    {
        using var handler = new BusyEndpoint(retryAfter, nextStatus);
        using var client = new HttpClient(handler);
        using var transport = new AiTransport(new("deepseek", "deepseek-flash"), client);
        var preview = new List<string>();
        var completion = transport.CompleteAsync([new JsonObject { ["role"] = "user", ["content"] = "happiness" }],
            [], preview.Add, CancellationToken.None);
        if (expectedRequests == 2 && nextStatus == HttpStatusCode.OK)
        {
            Assert.Equal("幸福", (await completion).Text);
            Assert.Equal(new[] { "幸福" }, preview);
        }
        else
        {
            Assert.Contains("HTTP 503", (await Assert.ThrowsAsync<AiException>(() => completion)).Message);
            Assert.Empty(preview);
        }
        Assert.Equal(expectedRequests, handler.Bodies.Count);
        Assert.Single(handler.Bodies.Distinct());
    }

    private sealed class BusyEndpoint(int retryAfter, HttpStatusCode nextStatus) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var response = new HttpResponseMessage(Bodies.Count == 1 ? HttpStatusCode.ServiceUnavailable : nextStatus);
            if (response.IsSuccessStatusCode)
                response.Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"幸福\"}}]}\n\ndata: [DONE]\n");
            else
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfter));
            return response;
        }
    }
}
