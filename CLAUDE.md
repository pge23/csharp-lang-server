# C# Language Server — Claude Instructions

## Symbol lookup

This repo is a C# MCP server with a live Roslyn workspace. For any task involving finding types, methods, properties, or other C# symbols **always prefer the MCP tools over grep or the Explore subagent**:

| Goal | Use |
|---|---|
| Locate a symbol by exact name | `mcp__csharp__symbol_search` (`patternMatch=false`) |
| Locate a symbol by partial name or camelCase hump | `mcp__csharp__symbol_search` (`patternMatch=true`) |
| Find all usages of a symbol | `mcp__csharp__find_references` |
| Find implementations of an interface/abstract member | `mcp__csharp__find_implementations` |
| Find call sites of a method | `mcp__csharp__find_callers` |
| Inspect type/member metadata | `mcp__csharp__get_symbol_info` |
| List all members of a type | `mcp__csharp__get_type_members` |

Use grep or Explore only for text patterns that are not symbol names (e.g. string literals, comments, arbitrary identifiers in non-C# files).

The MCP tools require loading their schema via `ToolSearch` before first use in a session — do this before reaching for grep.
