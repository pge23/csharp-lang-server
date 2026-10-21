using System.Text.Json;
using CSharpLangServer;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

if (args.Length < 1)
{
    await Console.Error.WriteLineAsync("Usage: CSharpLangServer <path-to-solution.sln>");
    return 1;
}

var solutionPath = Path.GetFullPath(args[0]);
if (!File.Exists(solutionPath))
{
    await Console.Error.WriteLineAsync($"Solution file not found: {solutionPath}");
    return 1;
}

// Must register MSBuild before any MSBuildWorkspace type is JITed.
try
{
    if (!MSBuildLocator.IsRegistered)
    {
        MSBuildLocator.RegisterDefaults();
    }
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"Failed to locate the .NET SDK: {ex.Message}");
    await Console.Error.WriteLineAsync("Install a compatible .NET SDK and ensure it is on PATH (run 'dotnet --info' to verify).");
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// stdio is the MCP transport; logs must go to stderr only.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton(sp =>
    new WorkspaceHolder(solutionPath, sp.GetRequiredService<ILoggerFactory>().CreateLogger<WorkspaceHolder>()));

builder.Services
    .AddMcpServer(options =>
    {
        options.Capabilities = new ServerCapabilities
        {
            Experimental = new Dictionary<string, object>
            {
                // JsonElement avoids the IDictionary/IReadOnlyDictionary ambiguity
                // in the MCP SDK's source-generated JSON serializer.
                ["claude/channel"] = JsonDocument.Parse("{}").RootElement,
            },
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

builder.Services.AddHostedService<WorkspaceLoaderService>();

var host = builder.Build();

await host.RunAsync();
return 0;
