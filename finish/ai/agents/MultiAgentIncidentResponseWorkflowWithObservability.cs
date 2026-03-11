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
#:package Microsoft.Extensions.Logging@10.0.0
#:package OpenTelemetry@1.15.0
#:package OpenTelemetry.Exporter.OpenTelemetryProtocol@1.15.0
#:package OpenTelemetry.Instrumentation.Http@1.15.0

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
const string ServiceName = "genai-incident-workflow";
const string SourceName = "GenAIDevOps.Observability";
const string MeterName = "GenAIDevOps.Observability";

var otlpEndpoint = new Uri(
    Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    ?? "http://localhost:4317");

var otlpProtocol = (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL") ?? "grpc")
    .Equals("grpc", StringComparison.OrdinalIgnoreCase)
    ? OtlpExportProtocol.Grpc
    : OtlpExportProtocol.HttpProtobuf;

var commonHeaders = NormalizeOtlpHeaders(
    Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS"),
    "OTEL_EXPORTER_OTLP_HEADERS",
    out var commonHeaderWarning);

var logsHeaders = NormalizeOtlpHeaders(
    Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_LOGS_HEADERS") ?? commonHeaders,
    "OTEL_EXPORTER_OTLP_LOGS_HEADERS",
    out var logsHeaderWarning);

var tracesHeaders = NormalizeOtlpHeaders(
    Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_HEADERS") ?? commonHeaders,
    "OTEL_EXPORTER_OTLP_TRACES_HEADERS",
    out var tracesHeaderWarning);

var metricsHeaders = NormalizeOtlpHeaders(
    Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_HEADERS") ?? commonHeaders,
    "OTEL_EXPORTER_OTLP_METRICS_HEADERS",
    out var metricsHeaderWarning);

foreach (var warning in new[] { commonHeaderWarning, logsHeaderWarning, tracesHeaderWarning, metricsHeaderWarning })
{
    if (!string.IsNullOrWhiteSpace(warning))
        Console.WriteLine($"[warn] {warning}");
}

var resourceBuilder = ResourceBuilder
    .CreateDefault()
    .AddService(ServiceName);

using var activitySource = new ActivitySource(SourceName);
using var meter = new Meter(MeterName, "1.0.0");

var workflowRuns = meter.CreateCounter<long>("ai.workflow.runs");
var workflowFailures = meter.CreateCounter<long>("ai.workflow.failures");
var agentResponses = meter.CreateCounter<long>("ai.agent.responses");
var evaluationScore = meter.CreateHistogram<double>("ai.evaluation.score");
var workflowDuration = meter.CreateHistogram<double>("ai.workflow.duration.ms");

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(resourceBuilder);
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
        options.AddOtlpExporter(otlp =>
        {
            otlp.Endpoint = otlpEndpoint;
            otlp.Protocol = otlpProtocol;
            // Always set headers explicitly so invalid env values don't get picked up by defaults.
            otlp.Headers = logsHeaders ?? string.Empty;
        });
    });

    builder.SetMinimumLevel(LogLevel.Information);
});

var logger = loggerFactory.CreateLogger("GenAIDevOps.Observability");

using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .SetSampler(new AlwaysOnSampler())
    .AddSource(SourceName)
    .AddSource("Microsoft.Extensions.AI")
    .AddSource("Microsoft.Agents.AI")
    .AddHttpClientInstrumentation()
    .AddOtlpExporter(o =>
    {
        o.Endpoint = otlpEndpoint;
        o.Protocol = otlpProtocol;
        o.Headers = tracesHeaders ?? string.Empty;
    })
    // temporary while debugging:
    // .AddConsoleExporter()
    .Build();

using var meterProvider = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .AddMeter(MeterName)
    .AddMeter("Microsoft.Extensions.AI")
    .AddMeter("Microsoft.Agents.AI")
    .AddOtlpExporter(o =>
    {
        o.Endpoint = otlpEndpoint;
        o.Protocol = otlpProtocol;
        o.Headers = metricsHeaders ?? string.Empty;
    })
    // temporary while debugging:
    // .AddConsoleExporter()
    .Build();

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var stopwatch = Stopwatch.StartNew();

workflowRuns.Add(1);
logger.LogInformation("Starting incident workflow demo");

// using (var startupActivity = activitySource.StartActivity("otel.startup.heartbeat", ActivityKind.Internal))
// {
//     startupActivity?.SetTag("otel.exporter.endpoint", otlpEndpoint.ToString());
//     startupActivity?.SetTag("otel.exporter.protocol", otlpProtocol.ToString());
// }
// meter.CreateCounter<long>("ai.otel.heartbeat").Add(1);

// if (string.Equals(Environment.GetEnvironmentVariable("OTEL_ONLY_TEST"), "true", StringComparison.OrdinalIgnoreCase))
// {
//     logger.LogInformation("OTEL_ONLY_TEST mode enabled. Emitting heartbeat telemetry only.");
//     tracerProvider.ForceFlush();
//     meterProvider.ForceFlush();
//     await Task.Delay(1000);
//     return;
// }

var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_ENDPOINT.");
var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_API_KEY.");
var deploymentName = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")
    ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("Set AZURE_OPENAI_DEPLOYMENT or AZURE_OPENAI_DEPLOYMENT_NAME.");

try
{
    using var rootActivity = activitySource.StartActivity("incident.workflow.run");
    rootActivity?.SetTag("ai.model.deployment", deploymentName);
    logger.LogInformation("Starting incident workflow run");

    var instrumentedChatClient = new AzureOpenAIClient(
        new Uri(endpoint),
        new System.ClientModel.ApiKeyCredential(apiKey))
        .GetChatClient(deploymentName)
        .AsIChatClient()
        .AsBuilder()
        .UseOpenTelemetry(
            loggerFactory: loggerFactory,
            sourceName: SourceName,
            configure: cfg => cfg.EnableSensitiveData = true)
        .Build();

    var incidentRoot = args.Length > 0
        ? args[0]
        : Path.Combine("ai", "samples", "incidents", "orderservice-sev2");

    string incidentJson;
    string pipelineLog;
    string appErrors;
    string serviceHealth;
    string runbook;

    using (var inputActivity = activitySource.StartActivity("incident.workflow.load-input"))
    {
        inputActivity?.SetTag("incident.root", incidentRoot);

        incidentJson = await ReadRequiredFileAsync(incidentRoot, "incident.json", cts.Token);
        pipelineLog = await ReadRequiredFileAsync(incidentRoot, "pipeline.log", cts.Token);
        appErrors = await ReadRequiredFileAsync(incidentRoot, "app-errors.log", cts.Token);
        serviceHealth = await ReadRequiredFileAsync(incidentRoot, "service-health.json", cts.Token);
        runbook = await ReadRequiredFileAsync(incidentRoot, "runbook.md", cts.Token);

        logger.LogInformation("Incident packet files loaded");
    }

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

    var promptsRoot = Path.Combine(Directory.GetCurrentDirectory(), "ai", "prompts");

    string pipelineInstructions = await File.ReadAllTextAsync(
        Path.Combine(promptsRoot, "incident-pipeline-analysis.prompt.md"), cts.Token);
    string appInstructions = await File.ReadAllTextAsync(
        Path.Combine(promptsRoot, "incident-app-analysis.prompt.md"), cts.Token);
    string runbookInstructions = await File.ReadAllTextAsync(
        Path.Combine(promptsRoot, "incident-runbook-analysis.prompt.md"), cts.Token);
    string commanderInstructions = await File.ReadAllTextAsync(
        Path.Combine(promptsRoot, "incident-commander.prompt.md"), cts.Token);

    var pipelineAgent = new ChatClientAgent(
        instrumentedChatClient,
        name: "PipelineAnalystAgent",
        instructions: pipelineInstructions);

    var appHealthAgent = new ChatClientAgent(
        instrumentedChatClient,
        name: "AppHealthAgent",
        instructions: appInstructions);

    var runbookAgent = new ChatClientAgent(
        instrumentedChatClient,
        name: "RunbookAgent",
        instructions: runbookInstructions);

    List<ChatMessage> workflowResult = [];

    using (var specialistActivity = activitySource.StartActivity("incident.workflow.specialists"))
    {
        specialistActivity?.SetTag("agent.count", 3);

        var workflow = AgentWorkflowBuilder.BuildConcurrent(
            [pipelineAgent, appHealthAgent, runbookAgent]);

        var inputMessages = new List<ChatMessage>
            {
                new(ChatRole.User, sharedIncidentPacket)
            };

        var run = await InProcessExecution.RunStreamingAsync(workflow, inputMessages);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (var evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            switch (evt)
            {
                case AgentResponseUpdateEvent update:
                    logger.LogInformation("Agent update from {ExecutorId}: {Data}", update.ExecutorId, update.Data);
                    Console.WriteLine($"[{update.ExecutorId}] {update.Data}");
                    break;

                case WorkflowOutputEvent outputEvent:
                    workflowResult = (List<ChatMessage>)outputEvent.Data!;
                    break;
            }
        }

        logger.LogInformation("Specialist workflow completed with {MessageCount} messages", workflowResult.Count);
    }

    if (workflowResult.Count == 0)
    {
        throw new InvalidOperationException("Workflow completed without aggregated output.");
    }

    var assistantOutputs = workflowResult
        .Where(m => m.Role == ChatRole.Assistant)
        .Select(m => m.Text?.ToString()?.Trim())
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Cast<string>()
        .ToList();

    agentResponses.Add(assistantOutputs.Count);

    IncidentCommanderResult finalResult;

    using (var commanderActivity = activitySource.StartActivity("incident.workflow.commander"))
    {
        commanderActivity?.SetTag("assistant.output.count", assistantOutputs.Count);

        var commanderAgent = new ChatClientAgent(
            instrumentedChatClient,
            name: "IncidentCommanderAgent",
            instructions: commanderInstructions);

        var commanderPrompt = $"""
            Incident packet:
            {incidentJson}

            Specialist outputs:
            {string.Join("\n\n", assistantOutputs)}

            Produce the final structured incident response.
            """;

        var commanderResponse =
            await commanderAgent.RunAsync<IncidentCommanderResult>(
                commanderPrompt,
                cancellationToken: cts.Token);

        finalResult = commanderResponse.Result;
        logger.LogInformation("Commander response received");
    }

    ValidateCommanderResult(finalResult);

    var eval = Evaluate(finalResult);
    evaluationScore.Record(eval.Score);

    rootActivity?.SetTag("ai.eval.score", eval.Score);
    rootActivity?.SetTag("ai.eval.passed", eval.Passed);

    Directory.CreateDirectory("artifacts");

    await File.WriteAllTextAsync(
        Path.Combine("artifacts", "incident-response.json"),
        JsonSerializer.Serialize(finalResult, new JsonSerializerOptions { WriteIndented = true }),
        cts.Token);

    await File.WriteAllTextAsync(
        Path.Combine("artifacts", "evaluation-summary.json"),
        JsonSerializer.Serialize(eval, new JsonSerializerOptions { WriteIndented = true }),
        cts.Token);

    Console.WriteLine("===== FINAL INCIDENT RESPONSE =====");
    Console.WriteLine(JsonSerializer.Serialize(finalResult, new JsonSerializerOptions { WriteIndented = true }));

    Console.WriteLine("===== EVALUATION SUMMARY =====");
    Console.WriteLine(JsonSerializer.Serialize(eval, new JsonSerializerOptions { WriteIndented = true }));

    logger.LogInformation("Incident workflow run completed successfully");
}
catch (Exception ex)
{
    workflowFailures.Add(1);
    Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
    logger.LogError(ex, "Workflow failed");
    throw;
}
finally
{
    stopwatch.Stop();
    workflowDuration.Record(stopwatch.Elapsed.TotalMilliseconds);

    tracerProvider.ForceFlush();
    meterProvider.ForceFlush();

    await Task.Delay(2000);
}

static async Task<string> ReadRequiredFileAsync(string root, string fileName, CancellationToken cancellationToken)
{
    var path = Path.Combine(root, fileName);
    if (!File.Exists(path))
        throw new FileNotFoundException($"Required file not found: {path}");

    return await File.ReadAllTextAsync(path, cancellationToken);
}

static string Truncate(string text, int maxChars)
{
    if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
        return text;

    return text[..maxChars] + "\n[...truncated for demo safety...]";
}

static string? NormalizeOtlpHeaders(string? rawHeaders, string sourceName, out string? warning)
{
    warning = null;

    if (string.IsNullOrWhiteSpace(rawHeaders))
        return null;

    var parts = rawHeaders.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var normalized = new List<string>();
    var dropped = 0;

    foreach (var part in parts)
    {
        var separatorIndex = part.IndexOf('=');
        if (separatorIndex < 0)
            separatorIndex = part.IndexOf(':');

        if (separatorIndex <= 0 || separatorIndex >= part.Length - 1)
        {
            dropped++;
            continue;
        }

        var key = part[..separatorIndex].Trim();
        var value = part[(separatorIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value) || key.Contains(' '))
        {
            dropped++;
            continue;
        }

        normalized.Add($"{key}={value}");
    }

    if (normalized.Count == 0)
    {
        warning = $"{sourceName} is set but invalid. Expected format: key=value,key2=value2. Headers will be ignored.";
        return null;
    }

    if (dropped > 0)
    {
        warning = $"Ignored {dropped} invalid OTLP header entr{(dropped == 1 ? "y" : "ies")}. Expected format: key=value,key2=value2.";
    }

    return string.Join(",", normalized);
}

static void ValidateCommanderResult(IncidentCommanderResult result)
{
    static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} must be non-empty.");
    }

    Require(result.IncidentId, nameof(result.IncidentId));
    Require(result.ExecutiveSummary, nameof(result.ExecutiveSummary));
    Require(result.LikelyCause, nameof(result.LikelyCause));
    Require(result.ImmediateAction, nameof(result.ImmediateAction));
    Require(result.RollbackRecommendation, nameof(result.RollbackRecommendation));
    Require(result.ValidationPlan, nameof(result.ValidationPlan));
    Require(result.StakeholderMessage, nameof(result.StakeholderMessage));
}

static EvaluationSummary Evaluate(IncidentCommanderResult result)
{
    var checks = new List<EvaluationCheck>
    {
        new("Has incident id", !string.IsNullOrWhiteSpace(result.IncidentId), 10),
        new("Has executive summary", !string.IsNullOrWhiteSpace(result.ExecutiveSummary), 15),
        new("Has likely cause", !string.IsNullOrWhiteSpace(result.LikelyCause), 15),
        new("Has immediate action", !string.IsNullOrWhiteSpace(result.ImmediateAction), 15),
        new("Has rollback recommendation", !string.IsNullOrWhiteSpace(result.RollbackRecommendation), 15),
        new("Has validation plan", !string.IsNullOrWhiteSpace(result.ValidationPlan), 15),
        new("Has stakeholder message", !string.IsNullOrWhiteSpace(result.StakeholderMessage), 10),
        new(
            "Validation plan mentions endpoint/metric verification",
            result.ValidationPlan.Contains("/orders", StringComparison.OrdinalIgnoreCase)
            || result.ValidationPlan.Contains("error", StringComparison.OrdinalIgnoreCase)
            || result.ValidationPlan.Contains("latency", StringComparison.OrdinalIgnoreCase),
            5)
    };

    var score = checks.Where(c => c.Passed).Sum(c => c.Weight);
    return new EvaluationSummary(score, score >= 80, checks);
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

public sealed record EvaluationCheck(string Name, bool Passed, int Weight);
public sealed record EvaluationSummary(int Score, bool Passed, IReadOnlyList<EvaluationCheck> Checks);