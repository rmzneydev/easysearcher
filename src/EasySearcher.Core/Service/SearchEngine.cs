using System.Diagnostics;

namespace EasySearcher.Core.Service;

/// <summary>
/// Motor completo: construye el indice en segundo plano, lo mantiene al dia con el USN Journal y guarda
/// los resultados de las ultimas busquedas para servirlos por paginas. Lo usa el servicio de Windows y,
/// cuando no hay servicio pero la app corre como administrador, la propia app.
/// </summary>
public sealed class SearchEngine : ISearchService
{
    const int MaxStoredSearches = 8;
    const int MaxCachedScopes = 16;

    readonly object _gate = new();
    readonly Dictionary<int, List<SearchResult>> _results = [];
    readonly Queue<int> _resultOrder = new();
    readonly Dictionary<string, SearchScope> _scopes = new(StringComparer.OrdinalIgnoreCase);
    readonly Stopwatch _buildClock = Stopwatch.StartNew();
    FileIndex? _index;
    string? _buildError;
    double _buildSeconds;
    long _changeCounter;
    int _nextSearchId;
    bool _disposed;

    /// <summary>Termina cuando el indice esta listo (o fallo su construccion).</summary>
    public Task Built { get; }

    public SearchEngine(IEnumerable<char> drives)
    {
        var letters = drives.ToArray();
        Built = Task.Run(() =>
        {
            try
            {
                var index = FileIndex.Build(letters);
                _buildSeconds = _buildClock.Elapsed.TotalSeconds;
                index.Changed += (_, _) => Interlocked.Increment(ref _changeCounter);
                index.StartWatching(TimeSpan.FromSeconds(1));
                lock (_gate)
                {
                    if (_disposed)
                    {
                        index.Dispose();
                        return;
                    }
                    _index = index;
                }
            }
            catch (Exception ex)
            {
                // Se informa por GetStatus; el proceso sigue vivo para poder mostrar el motivo
                _buildError = ex.Message;
            }
        });
    }

    public EngineStatus GetStatus()
    {
        var index = _index;
        if (index is null)
        {
            return new EngineStatus(false, 0, 0, [], [], _buildError is null ? [] : [_buildError],
                _buildClock.Elapsed.TotalSeconds);
        }

        return new EngineStatus(
            true,
            index.Count,
            Interlocked.Read(ref _changeCounter),
            index.Volumes.Select(v => v.Drive.ToString()).ToArray(),
            index.Volumes.Where(v => !v.IsLive).Select(v => v.Drive.ToString()).ToArray(),
            index.Failures.Select(f => $"{f.Key}: {f.Value.Message}").ToArray(),
            _buildSeconds);
    }

    public string ResolveRoot(string root) => GetScope(RequireIndex(), root).RootPath;

    public SearchResponse Search(SearchRequest request)
    {
        var index = RequireIndex();
        var scope = string.IsNullOrWhiteSpace(request.Root) ? null : GetScope(index, request.Root);

        var sw = Stopwatch.StartNew();
        var results = index.Search(request.Text, new SearchOptions
        {
            Kind = request.Kind,
            Scope = scope,
            IncludeSystem = request.IncludeSystem,
        });
        ResultSorter.Sort(results, request.Sort, request.Descending);
        double elapsed = sw.Elapsed.TotalMilliseconds;

        lock (_gate)
        {
            if (request.PreviousSearchId != 0 &&
                _results.TryGetValue(request.PreviousSearchId, out var previous) &&
                previous.SequenceEqual(results))
            {
                return new SearchResponse(request.PreviousSearchId, previous.Count, scope?.RootPath, true, elapsed);
            }

            int id = ++_nextSearchId;
            _results[id] = results;
            _resultOrder.Enqueue(id);
            while (_resultOrder.Count > MaxStoredSearches)
                _results.Remove(_resultOrder.Dequeue());
            return new SearchResponse(id, results.Count, scope?.RootPath, false, elapsed);
        }
    }

    public RowData[] GetRows(int searchId, int start, int count) =>
        Slice(searchId, start, count, r => new RowData(r.Volume.Drive, r.Entry, r.Name, r.DirectoryPath, r.IsDirectory));

    public RowDetails[] GetDetails(int searchId, int start, int count) =>
        Slice(searchId, start, count, r =>
        {
            var d = r.Details;
            return new RowDetails(d.Size, d.Modified);
        });

    T[] Slice<T>(int searchId, int start, int count, Func<SearchResult, T> map)
    {
        List<SearchResult>? results;
        lock (_gate) _results.TryGetValue(searchId, out results);
        if (results is null || start < 0 || start >= results.Count) return [];

        int end = Math.Min(results.Count, start + Math.Max(0, count));
        var rows = new T[end - start];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = map(results[start + i]);
        return rows;
    }

    FileIndex RequireIndex() => _index ?? throw new SearchServiceException(
        _buildError is null ? "El indice todavia se esta construyendo." : $"No se pudo construir el indice: {_buildError}");

    SearchScope GetScope(FileIndex index, string root)
    {
        lock (_gate)
        {
            if (_scopes.TryGetValue(root, out var cached)) return cached;
        }

        SearchScope scope;
        try
        {
            scope = index.CreateScope(root);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            throw new SearchServiceException(ex.Message);
        }

        lock (_gate)
        {
            if (_scopes.Count >= MaxCachedScopes) _scopes.Clear();
            _scopes[root] = scope;
        }
        return scope;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _index?.Dispose();
            _index = null;
        }
    }
}
