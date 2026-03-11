using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DevOps.McpServer.GitHub;

[McpServerToolType]
public sealed class GitHubTooling(GitHubApiClient api, ILogger<GitHubTooling> log)
{
    [McpServerTool(Name = "github_get_pull_request")]
    [Description("Get a pull request by number (includes title, body, state, author, head/base refs).")]
    public async Task<JsonElement> GetPullRequest(
        [Description("Repository owner (org or user).")] string owner,
        [Description("Repository name without .git.")] string repo,
        [Description("Pull request number.")] int number,
        CancellationToken ct)
    {
        api.EnsureAuthenticated();
        var url = $"repos/{owner}/{repo}/pulls/{number}";
        return await api.GetJson(url, ct);
    }

    [McpServerTool(Name = "github_list_pull_request_files")]
    [Description("List changed files for a pull request (filename, status, additions, deletions, patch).")]
    public async Task<JsonElement> ListPullRequestFiles(
        [Description("Repository owner (org or user).")] string owner,
        [Description("Repository name without .git.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("Page number (1-based).")] int page = 1,
        [Description("Items per page (max 100).")] int perPage = 100,
        CancellationToken ct = default)
    {
        api.EnsureAuthenticated();
        var url = $"repos/{owner}/{repo}/pulls/{number}/files?page={page}&per_page={perPage}";
        return await api.GetJson(url, ct);
    }

    [McpServerTool(Name = "github_add_pull_request_comment")]
    [Description("Add a PR review comment as an issue comment on the pull request conversation.")]
    public async Task<JsonElement> AddPullRequestComment(
        [Description("Repository owner (org or user).")] string owner,
        [Description("Repository name without .git.")] string repo,
        [Description("Pull request number.")] int number,
        [Description("Markdown comment body.")] string body,
        CancellationToken ct)
    {
        api.EnsureAuthenticated();
        log.LogInformation("Adding PR comment: {Owner}/{Repo} PR#{Number}", owner, repo, number);

        // PR issue comments use issues endpoint
        var url = $"repos/{owner}/{repo}/issues/{number}/comments";
        return await api.PostJson(url, new { body }, ct);
    }
}