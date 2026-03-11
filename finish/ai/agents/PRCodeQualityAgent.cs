#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property TreatWarningsAsErrors=true
#:property PublishAot=false

#:package Azure.AI.OpenAI@2.8.0-beta.1
#:package Azure.Identity@1.13.2
#:package Microsoft.Agents.AI.OpenAI@1.0.0-rc3
#:package Microsoft.Extensions.AI@10.3.0
#:package ModelContextProtocol@1.0.0

using System.ComponentModel;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OpenAI.Chat;

// ----------------------------
// Environment / inputs
// ----------------------------
var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_ENDPOINT");

var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_API_KEY");

var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
    ?? "gpt-4o-mini";

var owner = Environment.GetEnvironmentVariable("MCP_PR_OWNER")
    ?? throw new InvalidOperationException("Set MCP_PR_OWNER");

var repo = Environment.GetEnvironmentVariable("MCP_PR_REPO")
    ?? throw new InvalidOperationException("Set MCP_PR_REPO");

var prNumberText = Environment.GetEnvironmentVariable("MCP_PR_NUMBER")
    ?? throw new InvalidOperationException("Set MCP_PR_NUMBER");

if (!int.TryParse(prNumberText, out var prNumber))
{
    throw new InvalidOperationException("MCP_PR_NUMBER must be an integer.");
}

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

// ----------------------------
// MCP-backed PR retrieval
// ----------------------------
await using var mcpClient = await CreateMcpClientAsync(cts.Token);
using var prFilesDoc = await GitHubMcp.ListPullRequestFilesAsync(
    mcpClient,
    owner,
    repo,
    prNumber,
    cts.Token);

DiffTools.InitializeFromPullRequestFiles(prFilesDoc.RootElement);
var fileInventory = DiffTools.ListChangedFiles(maxFiles: 250);
var highRiskCandidates = DiffTools.GetTopRiskyFiles(maxFiles: 20);

// ----------------------------
// Agent instructions
// ----------------------------
var promptFile = Path.Combine(Directory.GetCurrentDirectory(), "ai", "prompts", "pr-diff-analysis.prompt.md");
var instructions = await File.ReadAllTextAsync(promptFile, cts.Token);

// Keep the model-facing tool names stable.
// The MCP server is the host-side retrieval boundary.
AIAgent agent =
    new AzureOpenAIClient(new Uri(endpoint), new System.ClientModel.ApiKeyCredential(apiKey))
        .GetChatClient(deploymentName)
        .AsAIAgent(
            name: "PRCodeQualityAgent",
            instructions: instructions,
            tools:
            [
                AIFunctionFactory.Create(DiffTools.ListChangedFiles),
                AIFunctionFactory.Create(DiffTools.GetTopRiskyFiles),
                AIFunctionFactory.Create(DiffTools.GetFileDiff)
            ]
        );

var prompt =
$"""
Analyze this pull request for code quality risks.

You already have a compact file inventory and high-risk candidates.
If you need deeper context, use tools to fetch only targeted file diffs.
Prefer focused retrieval to minimize tokens while preserving accuracy.

Call GetFileDiff when any of these are true:
- You are about to report a medium/high issue and cannot cite changed lines precisely.
- A high-risk file candidate is present and you need exact patch context.
- You suspect auth, input validation, or secret handling changes and need exact evidence.

Do not paste or quote diff/code content in output fields.
Refer to file path and approximate line number only.
Use hunk headers (@@ -a,b +c,d @@) to estimate new-file line numbers when possible.
If you cannot determine a reliable line number, set line to 0.

Do not execute commands.
Do not include markdown.

--- FILE INVENTORY (JSON) ---
{fileInventory}

--- HIGH RISK CANDIDATES (JSON) ---
{highRiskCandidates}
""";

var response = await agent.RunAsync(prompt, cancellationToken: cts.Token);

// ----------------------------
// Deterministic validation layer
// ----------------------------
try
{
    using var doc = JsonDocument.Parse(response.Text);
    var root = doc.RootElement;

    ResponseValidator.ValidateOrThrow(root);
    Console.WriteLine(root.GetRawText());
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        summary = "Agent returned invalid JSON",
        overallRisk = "medium",
        issues = new[]
        {
            new
            {
                file = "unknown",
                line = 0,
                severity = "medium",
                category = "maintainability",
                description = "The model response did not match the required schema.",
                suggestedFix = "Tighten schema constraints and reduce prompt size before inference."
            }
        },
        missingTests = new[]
        {
            "Add validation tests for agent response schema and fallback behavior"
        },
        confidence = 0.0
    }, new JsonSerializerOptions { WriteIndented = true }));
}

// ----------------------------
// MCP helpers
// ----------------------------
static async Task<McpClient> CreateMcpClientAsync(CancellationToken ct)
{
    var serverProject = Environment.GetEnvironmentVariable("MCP_SERVER_PROJECT")
        ?? "mcp/DevOps.McpServer";

    var clientTransport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Name = "DevOps MCP Server",
        Command = "dotnet",
        Arguments = ["run", "--project", serverProject]
    });

    return await McpClient.CreateAsync(clientTransport, cancellationToken: ct);
}

static class GitHubMcp
{
    public static async Task<JsonDocument> ListPullRequestFilesAsync(
        McpClient client,
        string owner,
        string repo,
        int number,
        CancellationToken ct)
    {
        var result = await client.CallToolAsync(
            "github_list_pull_request_files",
            new Dictionary<string, object?>
            {
                ["owner"] = owner,
                ["repo"] = repo,
                ["number"] = number
            },
            cancellationToken: ct);

        var text = ExtractText(result);

        return JsonDocument.Parse(text);
    }

    private static string ExtractText(CallToolResult result)
    {
        var textBlocks = result.Content.OfType<TextContentBlock>().ToList();

        if (textBlocks.Count == 0)
        {
            throw new InvalidOperationException("MCP tool returned no text content.");
        }

        return string.Join("\n", textBlocks.Select(t => t.Text));
    }
}

// ----------------------------
// Response validation
// ----------------------------
static class ResponseValidator
{
    private static readonly HashSet<string> AllowedOverallRisk = ["low", "medium", "high"];
    private static readonly HashSet<string> AllowedSeverity = ["low", "medium", "high"];
    private static readonly HashSet<string> AllowedCategory = ["security", "performance", "maintainability", "correctness", "testing"];

    public static void ValidateOrThrow(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Response root must be a JSON object.");
        }

        _ = GetRequiredString(root, "summary");

        var overallRisk = GetRequiredString(root, "overallRisk").ToLowerInvariant();
        if (!AllowedOverallRisk.Contains(overallRisk))
        {
            throw new InvalidOperationException($"overallRisk must be one of: {string.Join('|', AllowedOverallRisk)}.");
        }

        if (!root.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("issues must be an array.");
        }

        foreach (var issue in issues.EnumerateArray())
        {
            if (issue.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Each issue must be an object.");
            }

            _ = GetRequiredString(issue, "file");
            _ = GetRequiredString(issue, "description");
            _ = GetRequiredString(issue, "suggestedFix");

            var severity = GetRequiredString(issue, "severity").ToLowerInvariant();
            if (!AllowedSeverity.Contains(severity))
            {
                throw new InvalidOperationException($"issue.severity must be one of: {string.Join('|', AllowedSeverity)}.");
            }

            var category = GetRequiredString(issue, "category").ToLowerInvariant();
            if (!AllowedCategory.Contains(category))
            {
                throw new InvalidOperationException($"issue.category must be one of: {string.Join('|', AllowedCategory)}.");
            }

            if (!issue.TryGetProperty("line", out var lineElement) || !lineElement.TryGetInt32(out var line))
            {
                throw new InvalidOperationException("issue.line must be an integer.");
            }

            if (line < 0)
            {
                throw new InvalidOperationException("issue.line must be non-negative.");
            }
        }

        if (!root.TryGetProperty("missingTests", out var missingTests) || missingTests.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("missingTests must be an array.");
        }

        foreach (var test in missingTests.EnumerateArray())
        {
            if (test.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(test.GetString()))
            {
                throw new InvalidOperationException("Each missingTests item must be a non-empty string.");
            }
        }

        if (!root.TryGetProperty("confidence", out var confidenceElement) || !confidenceElement.TryGetDouble(out var confidence))
        {
            throw new InvalidOperationException("confidence must be a number.");
        }

        if (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence < 0 || confidence > 1)
        {
            throw new InvalidOperationException("confidence must be between 0 and 1.");
        }
    }

    private static string GetRequiredString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"{property} must be a string.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"{property} must be a non-empty string.");
        }

        return text.Trim();
    }
}

// ----------------------------
// Local model-facing tools
// ----------------------------
static class DiffTools
{
    private const int MaxResponseChars = 20_000;
    private static readonly object SyncRoot = new();
    private static List<FileDiffEntry> _entries = [];
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public static void InitializeFromPullRequestFiles(JsonElement files)
    {
        lock (SyncRoot)
        {
            _entries = [];

            foreach (var file in files.EnumerateArray())
            {
                var filePath = file.GetProperty("filename").GetString() ?? "unknown";
                var addedLines = file.TryGetProperty("additions", out var additions) ? additions.GetInt32() : 0;
                var removedLines = file.TryGetProperty("deletions", out var deletions) ? deletions.GetInt32() : 0;
                var patch = file.TryGetProperty("patch", out var patchEl) ? patchEl.GetString() ?? "" : "";
                var hunkCount = patch.Split('\n').Count(l => l.StartsWith("@@", StringComparison.Ordinal));
                var estimatedRiskScore = EstimateRiskScore(filePath, addedLines, removedLines, hunkCount);

                _entries.Add(new FileDiffEntry(
                    filePath,
                    patch,
                    addedLines,
                    removedLines,
                    hunkCount,
                    estimatedRiskScore));
            }
        }
    }

    [Description("List changed files with lightweight stats from the pull request. Use this first before requesting file-level diff details.")]
    public static string ListChangedFiles(
        [Description("Maximum number of files to return.")] int maxFiles = 200)
    {
        lock (SyncRoot)
        {
            var limited = _entries
                .Take(Math.Max(1, maxFiles))
                .Select(entry => new
                {
                    file = entry.FilePath,
                    addedLines = entry.AddedLines,
                    removedLines = entry.RemovedLines,
                    hunkCount = entry.HunkCount,
                    estimatedRiskScore = entry.EstimatedRiskScore
                });

            return SerializeToolPayloadWithCap(limited, "ListChangedFiles");
        }
    }

    [Description("Return a ranked subset of files likely to carry higher risk based on path and change volume. Use this to decide where to drill down.")]
    public static string GetTopRiskyFiles(
        [Description("Maximum number of files to return.")] int maxFiles = 15)
    {
        lock (SyncRoot)
        {
            var ranked = _entries
                .OrderByDescending(entry => entry.EstimatedRiskScore)
                .ThenByDescending(entry => entry.AddedLines + entry.RemovedLines)
                .Take(Math.Max(1, maxFiles))
                .Select(entry => new
                {
                    file = entry.FilePath,
                    addedLines = entry.AddedLines,
                    removedLines = entry.RemovedLines,
                    hunkCount = entry.HunkCount,
                    estimatedRiskScore = entry.EstimatedRiskScore
                });

            return SerializeToolPayloadWithCap(ranked, "GetTopRiskyFiles");
        }
    }

    [Description("Get the cached diff patch for a single file path from the pull request files response.")]
    public static string GetFileDiff(
        [Description("Exact file path as returned by ListChangedFiles.")] string filePath,
        [Description("Maximum characters to return.")] int maxChars = 12000)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "{\"error\":\"filePath is required\"}";
        }

        FileDiffEntry? entry;

        lock (SyncRoot)
        {
            entry = _entries.FirstOrDefault(item =>
                item.FilePath.Equals(filePath.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (entry is null)
        {
            return SerializeToolPayloadWithCap(
                new
                {
                    error = "file not found in PR",
                    requestedFile = filePath
                },
                "GetFileDiff");
        }

        if (string.IsNullOrWhiteSpace(entry.DiffText))
        {
            return SerializeToolPayloadWithCap(
                new
                {
                    file = entry.FilePath,
                    addedLines = entry.AddedLines,
                    removedLines = entry.RemovedLines,
                    hunkCount = entry.HunkCount,
                    estimatedRiskScore = entry.EstimatedRiskScore,
                    diffUnavailable = true,
                    reason = "GitHub did not provide patch content for this file in the pull request files response."
                },
                "GetFileDiff");
        }

        var boundedChars = Math.Clamp(maxChars, 1_000, MaxResponseChars);
        var body = entry.DiffText.Length <= boundedChars
            ? entry.DiffText
            : entry.DiffText[..boundedChars] + "\n[...truncated...]";

        return SerializeToolPayloadWithCap(
            new
            {
                file = entry.FilePath,
                addedLines = entry.AddedLines,
                removedLines = entry.RemovedLines,
                hunkCount = entry.HunkCount,
                estimatedRiskScore = entry.EstimatedRiskScore,
                diff = body
            },
            "GetFileDiff");
    }

    private static string SerializeToolPayloadWithCap(object payload, string source)
    {
        var serialized = JsonSerializer.Serialize(payload, CompactJson);
        if (serialized.Length <= MaxResponseChars)
        {
            return serialized;
        }

        var truncatedPayload = new
        {
            truncated = true,
            source,
            maxResponseChars = MaxResponseChars,
            originalLength = serialized.Length,
            message = "Tool payload exceeded max response size. Narrow query scope (fewer files or lower maxChars).",
            preview = serialized[..Math.Min(2_000, serialized.Length)]
        };

        var truncatedSerialized = JsonSerializer.Serialize(truncatedPayload, CompactJson);
        return truncatedSerialized.Length <= MaxResponseChars
            ? truncatedSerialized
            : truncatedSerialized[..MaxResponseChars];
    }

    private static int EstimateRiskScore(string filePath, int addedLines, int removedLines, int hunkCount)
    {
        var score = addedLines + removedLines + (hunkCount * 3);
        var normalizedPath = filePath.ToLowerInvariant();

        if (normalizedPath.Contains("auth") || normalizedPath.Contains("security"))
        {
            score += 40;
        }

        if (normalizedPath.Contains("workflow") ||
            normalizedPath.Contains("pipeline") ||
            normalizedPath.Contains("infra") ||
            normalizedPath.EndsWith(".bicep", StringComparison.Ordinal))
        {
            score += 20;
        }

        if (normalizedPath.EndsWith(".cs", StringComparison.Ordinal) ||
            normalizedPath.EndsWith(".ts", StringComparison.Ordinal) ||
            normalizedPath.EndsWith(".js", StringComparison.Ordinal) ||
            normalizedPath.EndsWith(".py", StringComparison.Ordinal))
        {
            score += 10;
        }

        return score;
    }

    private sealed record FileDiffEntry(
        string FilePath,
        string DiffText,
        int AddedLines,
        int RemovedLines,
        int HunkCount,
        int EstimatedRiskScore);
}