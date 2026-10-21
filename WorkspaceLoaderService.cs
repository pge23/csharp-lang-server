using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Text.Json.Serialization;

namespace CSharpLangServer;

internal sealed class WorkspaceLoaderService : IHostedService
{
    private readonly WorkspaceHolder _workspace;
    private readonly McpServer _server;
    private readonly ILogger<WorkspaceLoaderService> _logger;

    public WorkspaceLoaderService(
        WorkspaceHolder workspace,
        McpServer server,
        ILogger<WorkspaceLoaderService> logger)
    {
        _workspace = workspace;
        _server = server;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = LoadWithProgressAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task LoadWithProgressAsync(CancellationToken ct)
    {
        // Wait for the MCP client to complete the initialize handshake.
        while (_server.ClientCapabilities is null && !ct.IsCancellationRequested)
            await Task.Delay(50, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        await SendChannelAsync("Indexing C# solution…", "loading", ct);

        var projectsDone = 0;
        var lastNotify = DateTime.UtcNow;
        var progress = new Progress<ProjectLoadProgress>(p =>
        {
            if (p.Operation != ProjectLoadOperation.Build) return;
            Interlocked.Increment(ref projectsDone);
            var now = DateTime.UtcNow;
            if ((now - lastNotify).TotalSeconds < 5) return;
            lastNotify = now;
            var n = projectsDone;
            _ = SendChannelAsync($"Indexing… {n} project(s) loaded so far", "loading", ct);
        });

        try
        {
            await _workspace.LoadAsync(progress, ct);
            await SendChannelAsync(
                "C# solution ready — workspace indexed and tools available",
                "ready", ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Solution load failed");
            await SendChannelAsync($"Failed to load C# solution: {ex.Message}", "error", ct);
        }
    }

    private Task SendChannelAsync(string content, string status, CancellationToken ct)
    {
        // McpServer inherits SendNotificationAsync from McpSession in ModelContextProtocol.Core.
        try
        {
            return _server.SendNotificationAsync(
                "notifications/claude/channel",
                new ChannelParams(content, new() { ["status"] = status }),
                null,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Channel notification not delivered");
            return Task.CompletedTask;
        }
    }

    private sealed record ChannelParams(
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("meta")] Dictionary<string, string> Meta);
}
