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

using System.ComponentModel;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_ENDPOINT");
var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_API_KEY");
var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
    ?? "gpt-4o-mini"; 

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

var diffPath = args.Length > 0 ? args[0] : "pr.diff";
var diffContent = File.Exists(diffPath)
    ? await File.ReadAllTextAsync(diffPath, cts.Token)
        : throw new FileNotFoundException($"Diff file not found: {diffPath}");

DiffTools.Initialize(diffContent);
var optimizedDiff = DiffContextOptimizer.BuildOptimizedContext(diffContent);
var fileInventory = DiffTools.ListChangedFiles(maxFiles: 250);
var highRiskCandidates = DiffTools.GetTopRiskyFiles(maxFiles: 20);

var promptFile = Path.Combine(Directory.GetCurrentDirectory(), "ai", "prompts", "pr-diff-analysis.prompt.md");
var instructions = await File.ReadAllTextAsync(promptFile, cts.Token);
        
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
Analyze this pull request diff for code quality risks.

You already have an optimized global diff excerpt and file inventory.
If you need deeper context, use tools to fetch only targeted file diffs.
Prefer focused retrieval to minimize tokens while preserving accuracy.

Call GetFileDiff when any of these are true:
- You are about to report a medium/high issue and cannot cite changed lines precisely.
- A high-risk file candidate is present but the relevant hunk is missing from the global excerpt.
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

--- BEGIN DIFF ---
{optimizedDiff}
--- END DIFF ---
""";

var response = await agent.RunAsync(prompt, cancellationToken: cts.Token);

// ----------------------------
// Deterministic validation layer (Lesson 1 “self-critique” enforced by code)
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
    // If JSON is invalid, emit safe fallback JSON (still deterministic).
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
                suggestedFix = "Tighten schema constraints and reduce diff size before inference."
            }
        },
        missingTests = new[]
        {
            "Add validation tests for agent response schema and fallback behavior"
        },
        confidence = 0.0
    }, new JsonSerializerOptions { WriteIndented = true }));
}

static class DiffContextOptimizer
{
    private const int MaxChars = 90_000;
    private const int MaxLines = 1_200;

    public static string BuildOptimizedContext(string rawDiff)
    {
        if (string.IsNullOrWhiteSpace(rawDiff))
        {
            return "[empty diff]";
        }

        var normalized = rawDiff.Replace("\r\n", "\n");
        if (normalized.Length <= MaxChars)
        {
            return normalized;
        }

        var lines = normalized.Split('\n', StringSplitOptions.None);
        var selected = new List<string>(capacity: Math.Min(lines.Length, MaxLines));
        var changedLineCounter = 0;

        foreach (var line in lines)
        {
            if (selected.Count >= MaxLines)
            {
                break;
            }

            var isMetadata = line.StartsWith("diff --git ", StringComparison.Ordinal)
                             || line.StartsWith("+++ ", StringComparison.Ordinal)
                             || line.StartsWith("--- ", StringComparison.Ordinal)
                             || line.StartsWith("@@", StringComparison.Ordinal);

            var isChangedLine = line.StartsWith("+", StringComparison.Ordinal) || line.StartsWith("-", StringComparison.Ordinal);

            if (isMetadata || isChangedLine)
            {
                selected.Add(line);
                if (isChangedLine)
                {
                    changedLineCounter++;
                }
            }
        }

        var compact = string.Join('\n', selected);
        if (compact.Length > MaxChars)
        {
            compact = compact[..MaxChars] + "\n[...truncated...]";
        }

        return $"""
[optimized diff context]
selectedLines: {selected.Count}
selectedChangedLines: {changedLineCounter}

{compact}
""";
    }
}

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

        var overallRisk = GetRequiredString(root, "overallRisk");
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

            var severity = GetRequiredString(issue, "severity");
            if (!AllowedSeverity.Contains(severity))
            {
                throw new InvalidOperationException($"issue.severity must be one of: {string.Join('|', AllowedSeverity)}.");
            }

            var category = GetRequiredString(issue, "category");
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

        return text.Trim().ToLowerInvariant();
    }
}

static class DiffTools
{
    private const int MaxResponseChars = 20_000;
    private static readonly object SyncRoot = new();
    private static List<FileDiffEntry> _entries = [];
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public static void Initialize(string rawDiff)
    {
        lock (SyncRoot)
        {
            _entries = ParseEntries(rawDiff);
        }
    }

    [Description("List changed files with lightweight stats from the PR diff. Use this first before requesting file-level diff details.")]
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

    [Description("Get the full unified diff block for a single file path from the PR diff.")]
    public static string GetFileDiff(
        [Description("Exact file path as returned by ListChangedFiles, e.g. src/OrderService/Program.cs.")] string filePath,
        [Description("Maximum characters to return.")] int maxChars = 12000)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "{\"error\":\"filePath is required\"}";
        }

        lock (SyncRoot)
        {
            var entry = _entries.FirstOrDefault(item =>
                item.FilePath.Equals(filePath.Trim(), StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                return SerializeToolPayloadWithCap(new
                {
                    error = "file not found in diff",
                    requestedFile = filePath
                }, "GetFileDiff");
            }

            var boundedChars = Math.Clamp(maxChars, 1_000, MaxResponseChars);
            var body = entry.DiffText.Length <= boundedChars
                ? entry.DiffText
                : entry.DiffText[..boundedChars] + "\n[...truncated...]";

            return SerializeToolPayloadWithCap(new
            {
                file = entry.FilePath,
                addedLines = entry.AddedLines,
                removedLines = entry.RemovedLines,
                hunkCount = entry.HunkCount,
                estimatedRiskScore = entry.EstimatedRiskScore,
                diff = body
            }, "GetFileDiff");
        }
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

    private static List<FileDiffEntry> ParseEntries(string rawDiff)
    {
        if (string.IsNullOrWhiteSpace(rawDiff))
        {
            return [];
        }

        var normalized = rawDiff.Replace("\r\n", "\n");
        var blocks = SplitDiffBlocks(normalized);
        var parsed = new List<FileDiffEntry>(blocks.Count);

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.None);
            var header = lines.FirstOrDefault(line => line.StartsWith("diff --git ", StringComparison.Ordinal));
            var filePath = ExtractFilePath(header) ?? "unknown";

            var addedLines = lines.Count(line => line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal));
            var removedLines = lines.Count(line => line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("---", StringComparison.Ordinal));
            var hunkCount = lines.Count(line => line.StartsWith("@@", StringComparison.Ordinal));
            var estimatedRiskScore = EstimateRiskScore(filePath, addedLines, removedLines, hunkCount);

            parsed.Add(new FileDiffEntry(filePath, block, addedLines, removedLines, hunkCount, estimatedRiskScore));
        }

        return parsed;
    }

    private static List<string> SplitDiffBlocks(string normalizedDiff)
    {
        var lines = normalizedDiff.Split('\n', StringSplitOptions.None);
        var blocks = new List<string>();
        var current = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal) && current.Count > 0)
            {
                blocks.Add(string.Join('\n', current));
                current.Clear();
            }

            current.Add(line);
        }

        if (current.Count > 0)
        {
            blocks.Add(string.Join('\n', current));
        }

        return blocks;
    }

    private static string? ExtractFilePath(string? diffHeader)
    {
        if (string.IsNullOrWhiteSpace(diffHeader))
        {
            return null;
        }

        var parts = diffHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
        {
            return null;
        }

        var right = parts[3];
        return right.StartsWith("b/", StringComparison.Ordinal) ? right[2..] : right;
    }

    private static int EstimateRiskScore(string filePath, int addedLines, int removedLines, int hunkCount)
    {
        var score = addedLines + removedLines + (hunkCount * 3);
        var normalizedPath = filePath.ToLowerInvariant();

        if (normalizedPath.Contains("auth") || normalizedPath.Contains("security"))
        {
            score += 40;
        }

        if (normalizedPath.Contains("workflow") || normalizedPath.Contains("pipeline") || normalizedPath.Contains("infra") || normalizedPath.EndsWith(".bicep", StringComparison.Ordinal))
        {
            score += 20;
        }

        if (normalizedPath.EndsWith(".cs", StringComparison.Ordinal) || normalizedPath.EndsWith(".ts", StringComparison.Ordinal) || normalizedPath.EndsWith(".js", StringComparison.Ordinal) || normalizedPath.EndsWith(".py", StringComparison.Ordinal))
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
        int EstimatedRiskScore
    );
}