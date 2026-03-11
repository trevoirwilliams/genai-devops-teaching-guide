using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DevOps.McpServer.GitHub;

public sealed class GitHubApiClient(HttpClient http, GitHubAuthOptions auth, ILogger<GitHubApiClient> log)
{
    public void EnsureAuthenticated()
    {
        if (string.IsNullOrWhiteSpace(auth.Token))
            throw new InvalidOperationException(
                "PR_ANALYSIS_TOKEN is not set. Configure a GitHub token before calling GitHub tools.");
    }

    public async Task<JsonElement> GetJson(string relativeUrl, CancellationToken ct)
    {
        // Normalize to avoid surprises: never allow leading slash
        relativeUrl = relativeUrl.TrimStart('/');

        using var resp = await http.GetAsync(relativeUrl, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);

            // Rate limit / auth hints
            resp.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining);
            resp.Headers.TryGetValues("Retry-After", out var retryAfter);

            log.LogWarning("GitHub API call failed: {Status} {Reason}. Remaining={Remaining} RetryAfter={RetryAfter}",
                (int)resp.StatusCode, resp.ReasonPhrase,
                remaining?.FirstOrDefault(), retryAfter?.FirstOrDefault());

            // Keep the exception message actionable for tool callers
            throw new InvalidOperationException(
                $"GitHub API failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    public async Task<JsonElement> PostJson(string relativeUrl, object payload, CancellationToken ct)
    {
        EnsureAuthenticated();
        relativeUrl = relativeUrl.TrimStart('/');

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var resp = await http.PostAsync(relativeUrl, content, ct);

        if (resp.StatusCode == HttpStatusCode.NoContent)
        {
            // Return a minimal JSON response for tool consistency
            using var doc = JsonDocument.Parse("""{"status":"ok"}""");
            return doc.RootElement.Clone();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"GitHub API failed: {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc2 = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc2.RootElement.Clone();
    }
}
