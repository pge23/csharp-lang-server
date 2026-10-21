# csharp-lang-server

A minimal Model Context Protocol (MCP) server that wraps Roslyn to give Claude Code (or any MCP client) semantic C# code navigation. Token-efficient alternative to grep for "find references / symbol search / find implementations" queries over a whole solution.

## Why not a language server plugin

Claude Code has a built-in LSP tool, and its official `csharp-lsp` plugin covers most of what this server does. It was evaluated alongside Microsoft's own Roslyn language server, and this server was kept for two reasons:

- **Trust.** `csharp-lsp` runs `csharp-ls`, a community project with a single maintainer, against proprietary source. This server runs only Microsoft's Roslyn workspace APIs and the official MCP C# SDK; the only other code is the few hundred lines in this repo.
- **Microsoft's Roslyn language server is not a drop-in.** It has no call hierarchy, so callers can't be found. Without a custom client it can only auto-load every `.csproj` under the repo root, which costs more time and memory. It also missed the references in a test project.

### Benchmarks

Measured on 2026-10-21 against a 432-project production solution (`.slnx`) on an Apple Silicon Mac. Each server was driven over stdio by a scripted client. Results are from one run, querying an internal repository interface with one implementation and 7 references, and one of its methods. "Cold" is the first query after load, "warm" the second.

| | This server (Roslyn 5.9) | csharp-ls 0.28 | Roslyn LSP 5.5 (Microsoft) |
|---|---|---|---|
| Time until ready | 64 s | 55 s | 138 s |
| Peak memory | 2.7 GB | 2.1 GB | 7.2 GB |
| References found (of 7) | 7 | 7 | 5 |
| Callers | Yes | Yes | Not supported |
| Exact name search, cold / warm | 13 / 7 ms | 331 / 361 ms | 979 / 22 ms |
| camelCase search `ICATR`, cold / warm | 7204 / 361 ms | 7960 / 239 ms | 21 / 22 ms |
| References, cold / warm | 103 / 9 ms | 1057 / 19 ms | 572 / 503 ms |
| Implementations, cold / warm | 4664 / 19 ms | 567 / 3 ms | 153 / 2 ms |
| Callers, cold / warm | 508 / 81 ms | 34 / 3 ms | n/a |
| Symbol info (LSP: hover), warm | 8 ms | 1 ms | 1 ms |
| Type members (LSP: document symbols), warm | 8 ms | 1 ms | 1 ms |
| Go to definition, diagnostics | No | Yes | Yes |
| Per-repo setup | `.mcp.json` with solution path | None | None |

Revisit this choice if Microsoft or Anthropic ship a first-party C# language server plugin with call hierarchy support.

## Tools

| Tool | Purpose |
|---|---|
| `find_references` | All references to a named symbol (definitions + references). |
| `symbol_search` | Source declarations matching a name. `patternMatch=false` (default) requires an exact name; `patternMatch=true` enables substring and camelCase hump matching (e.g. `SC` matches `SystemClock`). Use before the other tools to disambiguate. |
| `find_implementations` | Concrete implementations of an interface, abstract class, or abstract/virtual member. |
| `find_callers` | All call sites of a method, constructor, or property (`is_direct` flag distinguishes direct calls from delegate/event indirection). |
| `get_symbol_info` | Rich metadata for a symbol: kind, accessibility, modifiers, containing type/namespace, XML doc summary, and for methods the return type and parameter list. |
| `get_type_members` | All declared members of a type (methods, properties, fields, events). `includeInherited: true` walks the base-type chain (excluding `object`). |
| `reload_workspace` | Force a full reload from disk after structural changes (new/removed files, `.csproj` edits). |

All tools take a symbol name (case-sensitive by default, `ignoreCase: true` to relax). Results are JSON with 1-based `line`/`column` and a trimmed `preview` of the source line.

`symbol_search` additionally accepts `patternMatch: bool` (default `false`). When `true` it delegates to Roslyn's `FindSourceDeclarationsWithPatternAsync`, which supports substring matching and camelCase hump abbreviations. Pattern matching is always case-insensitive; the `ignoreCase` parameter is ignored when `patternMatch=true`.

## Reducing friction with Claude Code

By default Claude Code may reach for `grep` or the Explore subagent for symbol lookups even when the MCP server is running. Two small additions to the **target repo** (the repo that contains your solution) eliminate that friction.

### 1. Allowlist the tools

Add the following to `.claude/settings.local.json` in the target repo so Claude never prompts for permission before calling a C# tool:

```json
{
  "permissions": {
    "allow": [
      "mcp__csharp__symbol_search",
      "mcp__csharp__find_references",
      "mcp__csharp__find_implementations",
      "mcp__csharp__find_callers",
      "mcp__csharp__get_symbol_info",
      "mcp__csharp__get_type_members",
      "mcp__csharp__reload_workspace"
    ]
  }
}
```

### 2. Add a CLAUDE.md preference rule

Create (or extend) a `CLAUDE.md` at the repo root to tell Claude to prefer the MCP tools over grep for symbol lookups:

```markdown
## Symbol lookup

For any task involving finding types, methods, properties, or other C# symbols
always prefer the MCP tools over grep or the Explore subagent:

| Goal | Use |
|---|---|
| Locate a symbol by exact name | `mcp__csharp__symbol_search` (`patternMatch=false`) |
| Locate a symbol by partial name or camelCase hump | `mcp__csharp__symbol_search` (`patternMatch=true`) |
| Find all usages of a symbol | `mcp__csharp__find_references` |
| Find implementations of an interface/abstract member | `mcp__csharp__find_implementations` |
| Find call sites of a method | `mcp__csharp__find_callers` |
| Inspect type/member metadata | `mcp__csharp__get_symbol_info` |
| List all members of a type | `mcp__csharp__get_type_members` |

Use grep or Explore only for text patterns that are not symbol names.
The MCP tools require loading their schema via `ToolSearch` before first use
in a session — do this before reaching for grep.
```

## Roadmap

### Tier 2

| Feature | Notes |
|---|---|
| `get_type_hierarchy` | Base classes + implemented interfaces (upward) and derived classes via `SymbolFinder.FindDerivedClassesAsync` (downward). Complements `find_implementations` with the full chain. |
| `get_diagnostics` | Compilation errors and warnings per file or project via `compilation.GetDiagnostics()`. Lets the model diagnose build failures without shelling out to `dotnet build`. |

### Tier 3

| Feature | Notes |
|---|---|
| `get_project_structure` | Solution → projects → namespaces → types outline. Good for orientation in a large unfamiliar solution; no file reading needed. |
| `resolve_at_location` | Given a file path + line number, return the symbol at that position via `SemanticModel.GetSymbolInfo`. Reverse of `symbol_search`; useful when the model is already looking at a file and wants semantic context on a token. |

## Prerequisites

- .NET 10 SDK (any `10.0.x`).
- An MSBuild-loadable solution (`.sln` or `.slnx`).

## Build

```bash
dotnet build
```

## Run

The server speaks MCP over stdio. You pass the solution path as the single CLI argument:

```bash
dotnet run --project CSharpLangServer.csproj -- path/to/Your.sln
```

The solution is loaded in the background after the MCP transport starts. Tool calls block until loading is complete, so they never return stale results. For a ~430-project solution, expect ~60–90 s before the first tool call completes.

Logs are written to **stderr** only (stdout is reserved for the MCP protocol).

## Register with Claude Code

Register the server in **project scope** in the repo that *contains the target solution* — not in the global `~/.claude/settings.json`. The solution path is a CLI arg (not baked into the binary), so each registration pins the server to exactly one solution. Registering globally exposes a `csharp` tool to every Claude Code session, including unrelated repos, and risks a 60–90 s stall if the tool is invoked outside the intended solution.

### Option A — `.mcp.json` (recommended)

Drop a `.mcp.json` file at the root of the target solution's repo. Claude Code discovers it automatically when the project is opened.

```json
{
  "mcpServers": {
    "csharp": {
      "command": "dotnet",
      "args": [
        "run",
        "--no-build",
        "--project",
        "/path/to/csharp-lang-server/CSharpLangServer.csproj",
        "--",
        "/path/to/your-solution.sln"
      ]
    }
  }
}
```

Because paths are machine-specific, add `.mcp.json` to `.gitignore` unless the server lives at a stable shared location (e.g. a dotfiles-style checkout that every developer places at the same path).

### Option B: `claude mcp add`

Run this from the target solution's repo to register the server in local scope (stored per project in `~/.claude.json`, not committed):

```bash
claude mcp add csharp --scope local -- dotnet run --no-build --project /path/to/csharp-lang-server/CSharpLangServer.csproj -- /path/to/your-solution.sln
```

---

Build once with `dotnet build` in the `csharp-lang-server` directory so `--no-build` works. Restart Claude Code to pick up any registration change. To serve multiple solutions at once, add one entry per solution with distinct names (e.g. `csharp-api`, `csharp-web`).

## Publish a standalone binary

If you don't want Claude Code to invoke `dotnet run` every time, publish for your platform's runtime identifier (`osx-arm64`, `linux-x64`, `win-x64`, ...):

```bash
dotnet publish -c Release -r osx-arm64 --self-contained false -o publish
```

Then use `publish/CSharpLangServer` (`publish/CSharpLangServer.exe` on Windows) as the `command` in your MCP registration, with the solution path as its only argument. The .NET 10 SDK must still be installed, since MSBuild is loaded from it at runtime.

## Smoke tests

Two hermetic smoke tests drive the server with a scripted MCP handshake over stdin:

- `smoke-test.sh` — against the bundled `samples/HelloLib.sln` fixture.
- `smoke-test-solution.sh <solution> <symbol> [wait-seconds]`: against any solution, calling `symbol_search`, `find_implementations` and `find_references` for one symbol. Large solutions need a longer wait (default 300 s).

Build first (`dotnet build`), then run on a Unix-ish shell (Git Bash / WSL work on Windows):

```bash
./smoke-test.sh
./smoke-test-solution.sh path/to/Your.slnx IMyService
```

Output: line-delimited JSON-RPC responses on stdout, logs in `smoke.stderr.log` (or `smoke.solution.stderr.log`).

## Project layout

```
Program.cs                stdio host + MSBuildLocator.RegisterDefaults()
WorkspaceHolder.cs        owns the MSBuildWorkspace/Solution
WorkspaceLoaderService.cs IHostedService that loads the workspace and emits channel notifications
NavigationTools.cs        the [McpServerTool] methods
samples/                  tiny HelloLib fixture (Greeter / IClock / SystemClock)
smoke-test*.sh            scripted smoke tests
```

## Notes

- `Microsoft.CodeAnalysis.CSharp.Workspaces` is a required companion to `Microsoft.CodeAnalysis.Workspaces.MSBuild` — without it `OpenSolutionAsync` fails with *"language 'C#' is not supported"*.
- `Microsoft.Build.Framework` (pulled in transitively by the Workspaces package) is referenced with `ExcludeAssets="runtime" PrivateAssets="all"` so `MSBuildLocator.RegisterDefaults()` can load the correct assemblies from the installed SDK at runtime.
- `.slnx` (XML solution format) is supported by the 5.9.0 Workspaces packages.
