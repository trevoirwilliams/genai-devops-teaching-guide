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

// ----------------------------
// Inputs / environment
// Azure OpenAI provider guidance is part of Agent Framework docs.
// ----------------------------
var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_ENDPOINT");
var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_API_KEY");
var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
    ?? "gpt-4o-mini";

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

var logPath = args.Length > 0 ? args[0] : "failure.log";
var logContent = File.Exists(logPath)
    ? await File.ReadAllTextAsync(logPath, cts.Token)
    : throw new FileNotFoundException($"Log file not found: {logPath}");

// Guard against oversized logs blowing past the model's context window.
const int maxLogChars = 100_000;
if (logContent.Length > maxLogChars)
{
    logContent = logContent[..maxLogChars] + "\n[...truncated...]";
}

AIAgent agent =
    new AzureOpenAIClient(new Uri(endpoint), new System.ClientModel.ApiKeyCredential(apiKey))
        .GetChatClient(deploymentName)
        .AsAIAgent(
            name: "DevOpsFailureTriageAgent",
            instructions:
            """
            You are a DevOps failure-triage agent running inside CI.
            RULES:
            - Use the tool ExtractFailureSignals first.
            - Output MUST be JSON only (no markdown, no prose).
            - Do not suggest destructive commands.
            JSON schema:
              {
                "failureCategory": "string",
                "severity": "low|medium|high",
                "likelyCause": "string",
                "recommendedNextStep": "string",
                "confidence": number
              }
            """,
            tools: [AIFunctionFactory.Create(LogTools.ExtractFailureSignals)]
        );

var prompt =
$"""
CI workflow failed. Here is the raw log content:
--- BEGIN LOG ---
{logContent}
--- END LOG ---

Steps:
1) Call ExtractFailureSignals(logContent).
2) Use those signals to classify the failure.
3) Return JSON only.
""";

var response = await agent.RunAsync(prompt, cancellationToken: cts.Token);

// ----------------------------
// Deterministic validation layer (Lesson 1 “self-critique” enforced by code)
// ----------------------------
try
{
    using var doc = JsonDocument.Parse(response.Text);

    // Validate all expected fields in the JSON schema.
    var root = doc.RootElement;
    _ = root.GetProperty("failureCategory").GetString();
    _ = root.GetProperty("severity").GetString();
    _ = root.GetProperty("likelyCause").GetString();
    _ = root.GetProperty("recommendedNextStep").GetString();
    _ = root.GetProperty("confidence").GetDouble();

    Console.WriteLine(root.GetRawText());
}
catch (Exception ex)
{
    // If JSON is invalid, emit safe fallback JSON (still deterministic).
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        failureCategory = "unknown",
        severity = "medium",
        likelyCause = "Agent returned invalid JSON",
        recommendedNextStep = "Inspect logs manually; tighten agent instructions; enforce tool-first and JSON-only output",
        confidence = 0.0,
        error = ex.Message
    }, new JsonSerializerOptions { WriteIndented = true }));
}

// ----------------------------
// Tool: deterministic extraction of failure signals from CI logs
// ----------------------------
static class LogTools
{
    [Description("Extract the most relevant failure signals from a CI log for troubleshooting and classification.")]
    public static string ExtractFailureSignals(
        [Description("Raw CI log content.")] string logContent,
        [Description("Maximum number of lines to return.")] int maxLines = 30)
    {
        if (string.IsNullOrWhiteSpace(logContent)) return "No log content provided.";

        var lines = logContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Keep it deterministic: focus on common failure markers.
        var markers = new[]
        {
            "ERROR", "FAIL", "FATAL", "Unhandled exception", "Build FAILED", "Test Run Failed", "Exited with code", "MSB1", "NETSDK", "NU1"
        };

        var hits = lines
            .Where(l => markers.Any(m => l.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Take(maxLines)
            .ToList();

        if (hits.Count == 0)
        {
            hits = lines.TakeLast(Math.Min(maxLines, lines.Length)).ToList();
        }

        return "=== Failure Signals ===\n" + string.Join('\n', hits);
    }
}