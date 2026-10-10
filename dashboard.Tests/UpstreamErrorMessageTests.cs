using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Pitstop.ResilienceDashboard;
using Xunit;

namespace Dashboard.Tests;

/// <summary>
/// The upstream explanation is surfaced from a known field, never by echoing
/// the body.
/// </summary>
/// <remarks>
/// PR #5 removed raw error bodies from our responses, which is right - they are
/// attacker-influenced and can carry request evidence. But "Groq returned 400."
/// with no reason is close to useless, and a mistyped model id is the likeliest
/// first failure an operator hits.
/// </remarks>
public class UpstreamErrorMessageTests : IDisposable
{
    private readonly string? _previousKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");

    public UpstreamErrorMessageTests() =>
        Environment.SetEnvironmentVariable("GROQ_API_KEY", "test-placeholder-not-a-real-key");

    public void Dispose() => Environment.SetEnvironmentVariable("GROQ_API_KEY", _previousKey);

    private static async Task<string> ErrorFor(string body, HttpStatusCode status)
    {
        using var app = new TestApp(StubAiHandler.For(groq: (status, body), ollama: null));
        using var client = app.CreateClient();
        var raw = await (await client.PostAsync("/api/ai/recommend", null)).Content.ReadFromJsonAsync<JsonNode>();
        return raw!["error"]!.GetValue<string>();
    }

    [Fact]
    public async Task Surfaces_a_structured_upstream_message()
    {
        var error = await ErrorFor(
            "{\"error\":{\"message\":\"model `openai/gpt-oss-99b` does not exist\"}}",
            HttpStatusCode.BadRequest);

        Assert.Contains("Upstream said:", error);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public async Task Ignores_a_body_that_is_not_the_expected_shape()
    {
        // No structured message means no explanation, rather than passing
        // through arbitrary upstream text.
        var error = await ErrorFor("<html><body>502 Bad Gateway</body></html>", HttpStatusCode.BadGateway);

        Assert.DoesNotContain("Upstream said:", error);
        Assert.DoesNotContain("html", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Never_echoes_a_raw_body_that_happens_to_contain_a_secret()
    {
        var error = await ErrorFor(
            "{\"debug\":\"Authorization: Bearer test-placeholder-not-a-real-key\"}",
            HttpStatusCode.Unauthorized);

        Assert.DoesNotContain("test-placeholder-not-a-real-key", error);
        Assert.DoesNotContain("Bearer", error);
        Assert.Contains("rejected the key", error);
    }

    [Fact]
    public async Task Bounds_an_enormous_upstream_message()
    {
        var huge = new string('x', 5000);
        var error = await ErrorFor($"{{\"error\":{{\"message\":\"{huge}\"}}}}", HttpStatusCode.BadRequest);

        Assert.True(error.Length < 400, $"error was {error.Length} chars");
    }

    [Fact]
    public async Task Still_names_the_recognised_failure_kinds()
    {
        Assert.Contains("rate limit", await ErrorFor("{}", HttpStatusCode.TooManyRequests));
        Assert.Contains("rejected the key", await ErrorFor("{}", HttpStatusCode.Unauthorized));
        Assert.Contains("denied access", await ErrorFor("{}", HttpStatusCode.Forbidden));
    }
}
