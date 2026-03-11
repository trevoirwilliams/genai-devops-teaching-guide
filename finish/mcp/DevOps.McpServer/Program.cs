using System.ComponentModel;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DevOps.McpServer.GitHub;

var builder = Host.CreateApplicationBuilder(args);

// IMPORTANT: keep stdout clean for JSON-RPC (stdio). Log to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var githubToken = Environment.GetEnvironmentVariable("PR_ANALYSIS_TOKEN");

builder.Services.AddSingleton(new GitHubAuthOptions(githubToken));
builder.Services.AddSingleton<GitHubTooling>();

builder.Services.AddHttpClient<GitHubApiClient>((sp, http) =>
{
    var auth = sp.GetRequiredService<GitHubAuthOptions>();

    http.BaseAddress = new Uri("https://api.github.com/");
    http.Timeout = TimeSpan.FromSeconds(30);

    // GitHub REST guidance: User-Agent is required. :contentReference[oaicite:6]{index=6}
    http.DefaultRequestHeaders.UserAgent.Clear();
    http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("genai-demo-finish-mcp-server", "1.0"));

    http.DefaultRequestHeaders.Accept.Clear();
    http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

    http.DefaultRequestHeaders.Remove("X-GitHub-Api-Version");
    http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

    // Only add Authorization if present.
    if (!string.IsNullOrWhiteSpace(auth.Token))
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
    }
});

// MCP server over STDIO, discover tools from assembly
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

public sealed record GitHubAuthOptions(string? Token);