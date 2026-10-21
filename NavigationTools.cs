using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace CSharpLangServer;

[McpServerToolType]
public sealed class NavigationTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WorkspaceHolder _workspace;

    public NavigationTools(WorkspaceHolder workspace)
    {
        _workspace = workspace;
    }

    [McpServerTool(Name = "find_references")]
    [Description(
        "Find all references to a C# symbol across the loaded solution. " +
        "Matches source declarations by name (exact, case-sensitive by default). " +
        "Returns a JSON object with a 'results' array of { symbol, kind, file, line, column, preview }, " +
        "where kind is 'reference' or 'definition' and line/column are 1-based. " +
        "The workspace automatically picks up file edits; call reload_workspace after adding/removing files or editing .csproj.")]
    public async Task<string> FindReferences(
        [Description("Symbol name — a type, method, property, field, or event. Case-sensitive. Use the simple name (e.g. 'MyService'), not a fully-qualified name.")]
        string symbol,
        [Description("If true, match symbol names case-insensitively. Default: false.")]
        bool ignoreCase = false,
        CancellationToken ct = default)
    {
        return await RunSafelyAsync("find_references", async () =>
        {
            var solution = await _workspace.GetCurrentSolutionAsync(ct);
            var declarations = await ResolveByNameAsync(solution, symbol, ignoreCase, ct);
            if (declarations.Count == 0) return NoMatches(symbol);

            var seen = new HashSet<(string path, int line, int col, string kind)>();
            var results = new List<object>();

            foreach (var sym in declarations)
            {
                var symbolDisplay = sym.ToDisplayString();
                var references = await SymbolFinder.FindReferencesAsync(sym, solution, ct);
                foreach (var referenced in references)
                {
                    foreach (var loc in referenced.Locations)
                    {
                        AddLocation(results, seen, symbolDisplay, loc.Location, "reference");
                    }
                    foreach (var def in referenced.Definition.Locations)
                    {
                        AddLocation(results, seen, symbolDisplay, def, "definition");
                    }
                }
            }

            return Serialize(new
            {
                matched_symbols = declarations.Count,
                results,
            });
        });
    }

    [McpServerTool(Name = "symbol_search")]
    [Description(
        "Find source declarations matching a name across the loaded solution. " +
        "Use this to locate a type/member before calling find_references or find_implementations. " +
        "Set patternMatch=true for substring/fuzzy matching (e.g. 'GetSrv' matches 'GetServiceRegistry'); " +
        "patternMatch=false (default) requires an exact name match. " +
        "Returns a 'results' array of { symbol, symbol_kind, file, line, column, preview }, " +
        "where symbol_kind is e.g. 'NamedType', 'Method', 'Property'. Line/column are 1-based.")]
    public async Task<string> SymbolSearch(
        [Description("Symbol name or pattern. Case-sensitive unless ignoreCase is true (ignored when patternMatch=true).")]
        string name,
        [Description("If true, match names case-insensitively. Default: false. Ignored when patternMatch=true.")]
        bool ignoreCase = false,
        [Description("If true, use substring/fuzzy pattern matching instead of exact name matching. Supports camelCase hump matching. Default: false.")]
        bool patternMatch = false,
        CancellationToken ct = default)
    {
        return await RunSafelyAsync("symbol_search", async () =>
        {
            var solution = await _workspace.GetCurrentSolutionAsync(ct);
            var declarations = patternMatch
                ? await ResolveByPatternAsync(solution, name, ct)
                : await ResolveByNameAsync(solution, name, ignoreCase, ct);
            if (declarations.Count == 0) return NoMatches(name);

            var seen = new HashSet<(string path, int line, int col, string kind)>();
            var results = new List<object>();

            foreach (var sym in declarations)
            {
                var symbolDisplay = sym.ToDisplayString();
                var symbolKind = sym is INamedTypeSymbol nt ? nt.TypeKind.ToString() : sym.Kind.ToString();

                foreach (var loc in sym.Locations)
                {
                    AddLocation(results, seen, symbolDisplay, loc, "definition", symbolKind);
                }
            }

            return Serialize(new
            {
                matched_symbols = declarations.Count,
                results,
            });
        });
    }

    [McpServerTool(Name = "find_implementations")]
    [Description(
        "Find concrete implementations of an interface, abstract class, or virtual/abstract member. " +
        "Matches the source declaration by name, then returns every implementing type or overriding member. " +
        "Returns a 'results' array of { source_symbol, implementation, file, line, column, preview }, 1-based positions.")]
    public async Task<string> FindImplementations(
        [Description("Name of an interface, abstract class, or abstract/virtual member.")]
        string symbol,
        [Description("If true, match symbol names case-insensitively. Default: false.")]
        bool ignoreCase = false,
        CancellationToken ct = default)
    {
        return await RunSafelyAsync("find_implementations", async () =>
        {
            var solution = await _workspace.GetCurrentSolutionAsync(ct);
            var declarations = await ResolveByNameAsync(solution, symbol, ignoreCase, ct);
            if (declarations.Count == 0) return NoMatches(symbol);

            var seen = new HashSet<(string path, int line, int col, string kind)>();
            var results = new List<object>();

            foreach (var sym in declarations)
            {
                var sourceDisplay = sym.ToDisplayString();
                var implementations = await SymbolFinder.FindImplementationsAsync(sym, solution, cancellationToken: ct);
                foreach (var impl in implementations)
                {
                    var implDisplay = impl.ToDisplayString();
                    foreach (var loc in impl.Locations)
                    {
                        if (!TryBuildLocation(loc, out var path, out var line, out var col, out var preview)) continue;
                        var key = (path, line, col, "implementation");
                        if (!seen.Add(key)) continue;
                        results.Add(new
                        {
                            source_symbol = sourceDisplay,
                            implementation = implDisplay,
                            file = path,
                            line,
                            column = col,
                            preview,
                        });
                    }
                }
            }

            return Serialize(new
            {
                matched_symbols = declarations.Count,
                results,
            });
        });
    }

    [McpServerTool(Name = "find_callers")]
    [Description(
        "Find all call sites of a C# method, constructor, or property across the loaded solution. " +
        "Returns a JSON object with a 'results' array of { symbol, caller, file, line, column, preview, is_direct }, " +
        "where 'caller' is the fully-qualified name of the calling method and 'is_direct' is false for calls " +
        "through delegates or event subscriptions. Line/column are 1-based.")]
    public async Task<string> FindCallers(
        [Description("Name of the method, constructor, or property to find callers of. Case-sensitive by default.")]
        string symbol,
        [Description("If true, match symbol names case-insensitively. Default: false.")]
        bool ignoreCase = false,
        CancellationToken ct = default)
    {
        return await RunSafelyAsync("find_callers", async () =>
        {
            var solution = await _workspace.GetCurrentSolutionAsync(ct);
            var declarations = await ResolveByNameAsync(solution, symbol, ignoreCase, ct);
            if (declarations.Count == 0) return NoMatches(symbol);

            var seen = new HashSet<(string path, int line, int col)>();
            var results = new List<object>();

            foreach (var sym in declarations)
            {
                var symbolDisplay = sym.ToDisplayString();
                var callers = await SymbolFinder.FindCallersAsync(sym, solution, ct);
                foreach (var callerInfo in callers)
                {
                    var callerDisplay = callerInfo.CallingSymbol.ToDisplayString();
                    foreach (var loc in callerInfo.Locations)
                    {
                        if (!TryBuildLocation(loc, out var path, out var line, out var col, out var preview)) continue;
                        if (!seen.Add((path, line, col))) continue;
                        results.Add(new
                        {
                            symbol = symbolDisplay,
                            caller = callerDisplay,
                            file = path,
                            line,
                            column = col,
                            preview,
                            is_direct = callerInfo.IsDirect,
                        });
                    }
                }
            }

            return Serialize(new { matched_symbols = declarations.Count, results });
        });
    }

    [McpServerTool(Name = "get_symbol_info")]
    [Description(
        "Return rich metadata for a named C# symbol: kind, accessibility, modifiers, containing type/namespace, " +
        "XML doc summary, and declaration location(s). For methods also returns the return type and parameter list. " +
        "Use this for 'what is X?' questions instead of reading source files.")]
    public async Task<string> GetSymbolInfo(
        [Description("Symbol name — type, method, property, field, or event. Case-sensitive by default.")]
        string symbol,
        [Description("If true, match symbol names case-insensitively. Default: false.")]
        bool ignoreCase = false,
        CancellationToken ct = default)
    {
        return await RunSafelyAsync("get_symbol_info", async () =>
        {
            var solution = await _workspace.GetCurrentSolutionAsync(ct);
            var declarations = await ResolveByNameAsync(solution, symbol, ignoreCase, ct);
            if (declarations.Count == 0) return NoMatches(symbol);

            var results = new List<object>();
            foreach (var sym in declarations)
            {
                var kind = sym is INamedTypeSymbol nt ? nt.TypeKind.ToString() : sym.Kind.ToString();

                var locations = new List<object>();
                foreach (var loc in sym.Locations)
                {
                    if (TryBuildLocation(loc, out var f, out var ln, out var col, out var prev))
                        locations.Add(new { file = f, line = ln, column = col, preview = prev });
                }

                string? returnType = null;
                List<string>? parameters = null;
                if (sym is IMethodSymbol method)
                {
                    returnType = method.ReturnType.ToDisplayString();
                    parameters = method.Parameters
                        .Select(p => $"{p.Type.ToDisplayString()} {p.Name}")
                        .ToList();
                }

                results.Add(new
                {
                    symbol = sym.ToDisplayString(),
                    kind,
                    accessibility = sym.DeclaredAccessibility.ToString(),
                    is_static = sym.IsStatic,
                    is_abstract = sym.IsAbstract,
                    is_virtual = sym.IsVirtual,
                    is_override = sym.IsOverride,
                    is_sealed = sym.IsSealed,
                    containing_type = sym.ContainingType?.ToDisplayString(),
                    containing_namespace = sym.ContainingNamespace is { IsGlobalNamespace: false } ns
                        ? ns.ToDisplayString() : null,
                    return_type = returnType,
                    parameters,
                    documentation = ExtractDocSummary(sym.GetDocumentationCommentXml()),
                    locations,
                });
            }

            return Serialize(new { matched_symbols = declarations.Count, results });
        });
    }

    [McpServerTool(Name = "get_type_members")]
    [Description(
        "List all members of a named type (class, interface, struct, enum): methods, properties, fields, events. " +
        "Returns a 'results' array of { name, kind, signature, accessibility, is_static, is_abstract, is_virtual, is_override }. " +
        "Set includeInherited to true to also include members from base types (excluding object).")]
    public async Task<string> GetTypeMembers(
        [Description("Name of the type. Case-sensitive by default.")]
        string typeName,
        [Description("If true, match type names case-insensitively. Default: false.")]
        bool ignoreCase = false,
        [Description("If true, include members inherited from base types (excluding object). Default: false.")]
        bool includeInherited = false,
        CancellationToken ct = default)
    {
        return await RunSafelyAsync("get_type_members", async () =>
        {
            var solution = await _workspace.GetCurrentSolutionAsync(ct);
            var declarations = await ResolveByNameAsync(solution, typeName, ignoreCase, ct);
            var typeSymbols = declarations.OfType<INamedTypeSymbol>().ToList();

            if (typeSymbols.Count == 0)
            {
                return declarations.Count > 0
                    ? Serialize(new { error = "not_a_type", message = $"'{typeName}' matched {declarations.Count} symbol(s) but none are named types." })
                    : NoMatches(typeName);
            }

            var sym = typeSymbols[0];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<object>();

            void AddMembers(IEnumerable<ISymbol> members)
            {
                foreach (var m in members)
                {
                    if (m.IsImplicitlyDeclared) continue;
                    var sig = m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    if (!seen.Add(sig)) continue;
                    results.Add(new
                    {
                        name = m.Name,
                        kind = m.Kind.ToString(),
                        signature = sig,
                        accessibility = m.DeclaredAccessibility.ToString(),
                        is_static = m.IsStatic,
                        is_abstract = m.IsAbstract,
                        is_virtual = m.IsVirtual,
                        is_override = m.IsOverride,
                    });
                }
            }

            AddMembers(sym.GetMembers());

            if (includeInherited)
            {
                var baseType = sym.BaseType;
                while (baseType is not null && baseType.SpecialType != SpecialType.System_Object)
                {
                    AddMembers(baseType.GetMembers());
                    baseType = baseType.BaseType;
                }
            }

            return Serialize(new
            {
                type = sym.ToDisplayString(),
                kind = sym.TypeKind.ToString(),
                member_count = results.Count,
                results,
            });
        });
    }

    [McpServerTool(Name = "reload_workspace")]
    [Description(
        "Force a full reload of the solution from disk. Call this after adding or removing files, " +
        "or after editing .csproj / .sln files. Modifications to existing .cs files are picked up " +
        "automatically — you do not need to call this for routine edits. " +
        "Expensive: on a large solution this takes tens of seconds.")]
    public async Task<string> ReloadWorkspace(CancellationToken ct = default)
    {
        return await RunSafelyAsync("reload_workspace", async () =>
        {
            var report = await _workspace.ReloadAsync(ct);
            return Serialize(new
            {
                reloaded = true,
                projects = report.Projects,
                documents = report.Documents,
            });
        });
    }

    private static async Task<List<ISymbol>> ResolveByNameAsync(
        Solution solution, string name, bool ignoreCase, CancellationToken ct)
    {
        var found = await SymbolFinder.FindSourceDeclarationsAsync(solution, name, ignoreCase, ct);
        return found.Distinct<ISymbol>(SymbolEqualityComparer.Default).ToList();
    }

    private static async Task<List<ISymbol>> ResolveByPatternAsync(
        Solution solution, string pattern, CancellationToken ct)
    {
        var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(solution, pattern, ct);
        return found.Distinct<ISymbol>(SymbolEqualityComparer.Default).ToList();
    }

    private static void AddLocation(
        List<object> results,
        HashSet<(string, int, int, string)> seen,
        string symbolDisplay,
        Location location,
        string kind,
        string? symbolKind = null)
    {
        if (!TryBuildLocation(location, out var path, out var line, out var col, out var preview)) return;
        if (!seen.Add((path, line, col, kind))) return;

        if (symbolKind is not null)
        {
            results.Add(new
            {
                symbol = symbolDisplay,
                symbol_kind = symbolKind,
                kind,
                file = path,
                line,
                column = col,
                preview,
            });
        }
        else
        {
            results.Add(new
            {
                symbol = symbolDisplay,
                kind,
                file = path,
                line,
                column = col,
                preview,
            });
        }
    }

    private static bool TryBuildLocation(
        Location location,
        out string filePath,
        out int line,
        out int column,
        out string preview)
    {
        filePath = string.Empty;
        line = 0;
        column = 0;
        preview = string.Empty;

        if (!location.IsInSource) return false;
        var tree = location.SourceTree;
        if (tree is null || string.IsNullOrEmpty(tree.FilePath)) return false;

        // Use the unmapped span + tree.FilePath so 'file', 'line', and 'preview' all refer
        // to the same physical source location. Mapped spans (from #line directives) would
        // point 'file' at generated-code targets while 'preview' read from the real tree.
        var span = location.GetLineSpan();
        var start = span.StartLinePosition;
        var text = tree.GetText();
        preview = start.Line < text.Lines.Count ? text.Lines[start.Line].ToString().Trim() : string.Empty;
        filePath = tree.FilePath;
        line = start.Line + 1;
        column = start.Character + 1;
        return true;
    }

    private static async Task<string> RunSafelyAsync(string toolName, Func<Task<string>> body)
    {
        try
        {
            return await body();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Serialize(new
            {
                error = ex.GetType().Name,
                message = ex.Message,
                tool = toolName,
            });
        }
    }

    private static string? ExtractDocSummary(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            return XDocument.Parse(xml).Descendants("summary").FirstOrDefault()?.Value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string NoMatches(string name) => Serialize(new
    {
        matched_symbols = 0,
        results = Array.Empty<object>(),
        message = $"No source declarations found matching '{name}'.",
    });

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, JsonOptions);
}
