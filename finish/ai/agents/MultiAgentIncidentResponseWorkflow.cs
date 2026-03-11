#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property TreatWarningsAsErrors=true
#:property PublishAot=false

#:package Azure.AI.OpenAI@2.8.0-beta.1
#:package Azure.Identity@1.13.2
#:package Microsoft.Agents.AI.OpenAI@1.0.0-rc3
#:package Microsoft.Agents.AI.Workflows@1.0.0-rc3
#:package Microsoft.Extensions.AI@10.3.0

using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

// ------------------------------------------------------------
// Environment / configuration
// ------------------------------------------------------------
var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_ENDPOINT.");

var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_API_KEY.");

var deploymentName =
    Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
    ?? "gpt-4o-mini";

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

// ------------------------------------------------------------
// Azure OpenAI -> IChatClient
// Current Agent Framework docs show this pattern for workflows.
// ------------------------------------------------------------
var chatClient = new AzureOpenAIClient(
        new Uri(endpoint),
        new System.ClientModel.ApiKeyCredential(apiKey))
    .GetChatClient(deploymentName)
    .AsIChatClient();

// ------------------------------------------------------------
// Input data
// ------------------------------------------------------------
var incidentRoot = args.Length > 0
    ? args[0]
    : Path.Combine("ai", "samples", "incidents", "orderservice-sev2");

string incidentJson = await ReadRequiredFileAsync(incidentRoot, "incident.json", cts.Token);
string pipelineLog = await ReadRequiredFileAsync(incidentRoot, "pipeline.log", cts.Token);
string appErrors = await ReadRequiredFileAsync(incidentRoot, "app-errors.log", cts.Token);
string serviceHealth = await ReadRequiredFileAsync(incidentRoot, "service-health.json", cts.Token);
string runbook = await ReadRequiredFileAsync(incidentRoot, "runbook.md", cts.Token);

// Keep payload bounded for a classroom-safe demo.
pipelineLog = Truncate(pipelineLog, 20_000);
appErrors = Truncate(appErrors, 20_000);
serviceHealth = Truncate(serviceHealth, 10_000);
runbook = Truncate(runbook, 20_000);

var sharedIncidentPacket = $"""
INCIDENT
{incidentJson}

PIPELINE LOG
{pipelineLog}

APPLICATION ERRORS
{appErrors}

SERVICE HEALTH
{serviceHealth}

RUNBOOK
{runbook}
""";

// ------------------------------------------------------------
// Prompt loading
// ------------------------------------------------------------
var promptsRoot = Path.Combine(Directory.GetCurrentDirectory(), "ai", "prompts");

string pipelineInstructions = await File.ReadAllTextAsync(
    Path.Combine(promptsRoot, "incident-pipeline-analysis.prompt.md"), cts.Token);

string appInstructions = await File.ReadAllTextAsync(
    Path.Combine(promptsRoot, "incident-app-analysis.prompt.md"), cts.Token);

string runbookInstructions = await File.ReadAllTextAsync(
    Path.Combine(promptsRoot, "incident-runbook-analysis.prompt.md"), cts.Token);

string commanderInstructions = await File.ReadAllTextAsync(
    Path.Combine(promptsRoot, "incident-commander.prompt.md"), cts.Token);

// ------------------------------------------------------------
// Specialist agents
// Agent Framework docs use ChatClientAgent for concurrent workflows.
// Give each agent an explicit unique name.
// ------------------------------------------------------------
var pipelineAgent = new ChatClientAgent(
    chatClient,
    pipelineInstructions,
    name: "PipelineAnalystAgent");

var appHealthAgent = new ChatClientAgent(
    chatClient,
    appInstructions,
    name: "AppHealthAgent");

var runbookAgent = new ChatClientAgent(
    chatClient,
    runbookInstructions,
    name: "RunbookAgent");

// ------------------------------------------------------------
// Build and run concurrent workflow
// ------------------------------------------------------------
var workflow = AgentWorkflowBuilder.BuildConcurrent(
    [pipelineAgent, appHealthAgent, runbookAgent]);

var inputMessages = new List<ChatMessage>
{
    new(ChatRole.User, sharedIncidentPacket)
};

StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, inputMessages);
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

List<ChatMessage> workflowResult = [];

await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
{
    switch (evt)
    {
        case AgentResponseUpdateEvent update:
            Console.WriteLine($"[{update.ExecutorId}] {update.Data}");
            break;

        case WorkflowOutputEvent outputEvent:
            workflowResult = (List<ChatMessage>)outputEvent.Data!;
            break;
    }
}

if (workflowResult.Count == 0)
{
    throw new InvalidOperationException("Workflow completed without aggregated output.");
}

Console.WriteLine();
Console.WriteLine("===== AGGREGATED SPECIALIST OUTPUTS =====");

var assistantOutputs = workflowResult
    .Where(m => m.Role == ChatRole.Assistant)
    .Select(GetMessageText)
    .Where(t => !string.IsNullOrWhiteSpace(t))
    .ToList();

foreach (var output in assistantOutputs)
{
    Console.WriteLine(output);
    Console.WriteLine();
}

// ------------------------------------------------------------
// Final commander agent
// Use typed structured output here for reliability.
// Agent Framework docs recommend RunAsync<T>() when the output
// type is known at compile time.
// ------------------------------------------------------------
AIAgent commanderAgent = new ChatClientAgent(
    chatClient,
    commanderInstructions,
    name: "IncidentCommanderAgent");

var commanderPrompt = $"""
Incident packet:
{incidentJson}

Specialist outputs:
{string.Join("\n\n", assistantOutputs)}

Produce the final structured incident response.
""";

AgentResponse<IncidentCommanderResult> commanderResponse =
    await commanderAgent.RunAsync<IncidentCommanderResult>(
        commanderPrompt,
        cancellationToken: cts.Token);

var finalResult = commanderResponse.Result;

// ------------------------------------------------------------
// Deterministic validation layer
// ------------------------------------------------------------
ValidateCommanderResult(finalResult);

Console.WriteLine("===== FINAL INCIDENT RESPONSE =====");
Console.WriteLine(JsonSerializer.Serialize(
    finalResult,
    new JsonSerializerOptions { WriteIndented = true }));

// ------------------------------------------------------------
// Helpers
// ------------------------------------------------------------
static async Task<string> ReadRequiredFileAsync(
    string root,
    string fileName,
    CancellationToken cancellationToken)
{
    var path = Path.Combine(root, fileName);
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Required file not found: {path}");
    }

    return await File.ReadAllTextAsync(path, cancellationToken);
}

static string Truncate(string text, int maxChars)
{
    if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
    {
        return text;
    }

    return text[..maxChars] + "\n[...truncated for demo safety...]";
}

static string GetMessageText(ChatMessage message)
{
    // Keep this simple and robust for demo output.
    var text = message.Text.ToString();
    return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
}

static void ValidateCommanderResult(IncidentCommanderResult result)
{
    static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} must be non-empty.");
        }
    }

    Require(result.IncidentId, nameof(result.IncidentId));
    Require(result.ExecutiveSummary, nameof(result.ExecutiveSummary));
    Require(result.LikelyCause, nameof(result.LikelyCause));
    Require(result.ImmediateAction, nameof(result.ImmediateAction));
    Require(result.RollbackRecommendation, nameof(result.RollbackRecommendation));
    Require(result.ValidationPlan, nameof(result.ValidationPlan));
    Require(result.StakeholderMessage, nameof(result.StakeholderMessage));
}

public sealed class IncidentCommanderResult
{
    public string IncidentId { get; set; } = string.Empty;
    public string ExecutiveSummary { get; set; } = string.Empty;
    public string LikelyCause { get; set; } = string.Empty;
    public string ImmediateAction { get; set; } = string.Empty;
    public string RollbackRecommendation { get; set; } = string.Empty;
    public string ValidationPlan { get; set; } = string.Empty;
    public string StakeholderMessage { get; set; } = string.Empty;
}