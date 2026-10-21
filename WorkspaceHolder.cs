using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace CSharpLangServer;

public sealed class WorkspaceHolder : IDisposable
{
    private readonly string _solutionPath;
    private readonly string _solutionDir;
    private readonly ILogger<WorkspaceHolder> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TaskCompletionSource _loadedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // File watcher state. _dirtyLock guards _dirtyPaths and _structuralReloadPending.
    private readonly object _dirtyLock = new();
    private readonly HashSet<string> _dirtyPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _structuralReloadPending;
    private FileSystemWatcher? _watcher;

    private MSBuildWorkspace? _workspace;
    private Solution? _solution;

    public WorkspaceHolder(string solutionPath, ILogger<WorkspaceHolder> logger)
    {
        _solutionPath = solutionPath;
        _solutionDir = Path.GetDirectoryName(solutionPath)
            ?? throw new ArgumentException("Solution path has no directory", nameof(solutionPath));
        _logger = logger;
    }

    public string SolutionPath => _solutionPath;

    public async Task LoadAsync(IProgress<ProjectLoadProgress>? progress = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await ReloadInternalAsync(progress, ct);
            StartWatcherIfNeeded();
            _loadedTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            _loadedTcs.TrySetException(ex);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns the current solution, applying any filesystem changes observed since the last call.
    /// Must be used instead of reading a cached Solution directly — tool calls will otherwise
    /// answer from a stale snapshot after the agent edits files.
    /// </summary>
    public async Task<Solution> GetCurrentSolutionAsync(CancellationToken ct = default)
    {
        await _loadedTcs.Task.WaitAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            await ApplyPendingChangesAsync(ct);
            return _solution!;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Force a full reload from disk. Use after structural changes (new/removed files, .csproj edits).
    /// </summary>
    public async Task<ReloadReport> ReloadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var report = await ReloadInternalAsync(null, ct);
            StartWatcherIfNeeded();
            return report;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ReloadReport> ReloadInternalAsync(IProgress<ProjectLoadProgress>? progress, CancellationToken ct)
    {
        _workspace?.Dispose();
        _workspace = MSBuildWorkspace.Create();
#pragma warning disable CS0618 // WorkspaceFailed is obsolete but still the simplest way to surface diagnostics
        _workspace.WorkspaceFailed += (_, e) =>
            _logger.LogWarning("Workspace diagnostic [{Kind}]: {Message}", e.Diagnostic.Kind, e.Diagnostic.Message);
#pragma warning restore CS0618

        _logger.LogInformation("Loading solution {Solution}", _solutionPath);
        _solution = await _workspace.OpenSolutionAsync(_solutionPath, progress, cancellationToken: ct);

        var projectCount = _solution.Projects.Count();
        var documentCount = _solution.Projects.Sum(p => p.Documents.Count());
        _logger.LogInformation("Loaded {Projects} projects, {Documents} documents", projectCount, documentCount);

        lock (_dirtyLock)
        {
            _dirtyPaths.Clear();
            _structuralReloadPending = false;
        }

        return new ReloadReport(projectCount, documentCount);
    }

    private async Task ApplyPendingChangesAsync(CancellationToken ct)
    {
        string[] paths;
        bool structural;
        lock (_dirtyLock)
        {
            if (_dirtyPaths.Count == 0 && !_structuralReloadPending) return;
            paths = _dirtyPaths.ToArray();
            structural = _structuralReloadPending;
            _dirtyPaths.Clear();
            _structuralReloadPending = false;
        }

        if (structural)
        {
            _logger.LogInformation("Structural change detected, reloading solution");
            await ReloadInternalAsync(null, ct);
            return;
        }

        var sol = _solution!;
        var updated = 0;
        foreach (var path in paths)
        {
            var docIds = sol.GetDocumentIdsWithFilePath(path);
            if (docIds.IsDefaultOrEmpty) continue;
            if (!File.Exists(path))
            {
                // File went away between watcher event and now — treat as structural.
                _logger.LogInformation("File disappeared during sync, reloading: {Path}", path);
                await ReloadInternalAsync(null, ct);
                return;
            }

            SourceText text;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                text = SourceText.From(stream);
            }
            catch (IOException ex)
            {
                // Could be a transient editor lock — leave dirty for next call.
                _logger.LogDebug(ex, "Deferring sync for locked file: {Path}", path);
                lock (_dirtyLock) _dirtyPaths.Add(path);
                continue;
            }

            foreach (var id in docIds)
            {
                sol = sol.WithDocumentText(id, text, PreservationMode.PreserveValue);
            }
            updated++;
        }

        _solution = sol;
        if (updated > 0) _logger.LogDebug("Synced {Count} changed document(s)", updated);
    }

    private void StartWatcherIfNeeded()
    {
        if (_watcher is not null) return;
        try
        {
            _watcher = new FileSystemWatcher(_solutionDir, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileCreatedOrDeleted;
            _watcher.Deleted += OnFileCreatedOrDeleted;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += (_, e) =>
                _logger.LogWarning(e.GetException(), "File watcher error; workspace may miss edits until next reload");
            _watcher.EnableRaisingEvents = true;
            _logger.LogInformation("Watching {Dir} for *.cs changes", _solutionDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start file watcher; edits will not be picked up automatically");
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_dirtyLock) _dirtyPaths.Add(e.FullPath);
    }

    private void OnFileCreatedOrDeleted(object sender, FileSystemEventArgs e)
    {
        lock (_dirtyLock) _structuralReloadPending = true;
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        lock (_dirtyLock) _structuralReloadPending = true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _workspace?.Dispose();
        _lock.Dispose();
    }
}

public readonly record struct ReloadReport(int Projects, int Documents);
