using System.Net;
using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

// Mocked HTTP only. No provider, Docker, or business data is contacted.
public class ProviderResponseSafetyTests : IDisposable
{
    private readonly string? previous = Environment.GetEnvironmentVariable("GROQ_API_KEY");
    public ProviderResponseSafetyTests() => Environment.SetEnvironmentVariable("GROQ_API_KEY", "synthetic-test-credential");
    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", previous);

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":{}}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":42}}]}")]
    [InlineData("not-json")]
    public async Task Invalid_groq_envelopes_return_failure(string envelope)
    {
        using var http = new HttpClient(new Handler(envelope));
        var result = await new GroqClient(http, new()).CompleteAsync("synthetic", "synthetic", "audit", new JsonObject(), default);
        Assert.False(result.Ok);
        Assert.Null(result.Content);
        Assert.NotEmpty(result.Error!);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"message\":[]}")]
    [InlineData("{\"message\":{\"content\":42}}")]
    [InlineData("not-json")]
    public async Task Invalid_ollama_envelopes_return_failure(string envelope)
    {
        using var http = new HttpClient(new Handler(envelope));
        var result = await new OllamaClient(http, new()).CompleteAsync("synthetic", "synthetic", "audit", new JsonObject(), default);
        Assert.False(result.Ok);
        Assert.Null(result.Content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Body_read_is_bounded_by_provider_timeout(bool cloud)
    {
        using var http = new HttpClient(new Handler("", slow: true));
        var options = new AiOptions { Groq = new() { TimeoutSeconds = 1 }, Ollama = new() { TimeoutSeconds = 1 } };
        IAiProvider provider = cloud ? new GroqClient(http, options) : new OllamaClient(http, options);
        var result = await provider.CompleteAsync("synthetic", "synthetic", "audit", new JsonObject(), default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Ok);
        Assert.Contains("timed out", result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Error_body_is_not_exposed(bool cloud)
    {
        using var http = new HttpClient(new Handler("synthetic-test-credential private-evidence", status: HttpStatusCode.Unauthorized));
        IAiProvider provider = cloud ? new GroqClient(http, new()) : new OllamaClient(http, new());
        var result = await provider.CompleteAsync("synthetic", "synthetic", "audit", new JsonObject(), default);
        Assert.False(result.Ok);
        Assert.DoesNotContain("synthetic-test-credential", result.Error);
        Assert.DoesNotContain("private-evidence", result.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Caller_cancellation_propagates(bool cloud)
    {
        using var http = new HttpClient(new Handler("", slow: true));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        IAiProvider provider = cloud ? new GroqClient(http, new()) : new OllamaClient(http, new());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.CompleteAsync("synthetic", "synthetic", "audit", new JsonObject(), cts.Token));
    }

    [Fact]
    public async Task Auto_falls_back_after_wrong_type_groq_envelope()
    {
        var good = Envelope.Ollama(Envelope.Recommendation("kill-service", "workshopmanagementapi", 25));
        using var app = new TestApp(StubAiHandler.For(groq: (HttpStatusCode.OK, "{\"choices\":[]}"),
                                                     ollama: (HttpStatusCode.OK, good)));
        using var client = app.CreateClient();
        await client.PostAsync("/api/ai/mode", new StringContent("{\"mode\":\"Auto\"}", System.Text.Encoding.UTF8, "application/json"));
        var response = await client.PostAsync("/api/ai/recommend", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ollama", body!["provider"]!.GetValue<string>());
        Assert.Empty(app.Runner.Invocations);
    }

    private sealed class Handler(string body, bool slow = false, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/tags"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent("{\"models\":[{\"name\":\"qwen3.5:4b\"}]}") });
            return Task.FromResult(new HttpResponseMessage(status) {
                Content = slow ? new SlowContent() : new StringContent(body) });
        }
    }

    private sealed class SlowContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new InvalidOperationException("Body must be read with a cancellation token");
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) =>
            await Task.Delay(Timeout.Infinite, ct);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
