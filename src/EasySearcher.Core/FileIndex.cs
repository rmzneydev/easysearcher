using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime;

namespace EasySearcher.Core;

/// <summary>Indice de varias unidades NTFS; cada una se lee en paralelo.</summary>
public sealed class FileIndex : IDisposable
{
    CancellationTokenSource? _watchCts;

    public IReadOnlyList<VolumeIndex> Volumes { get; }

    /// <summary>Unidades que no se pudieron indexar y el motivo.</summary>
    public IReadOnlyDictionary<char, Exception> Failures { get; }

    public int Count => Volumes.Sum(v => v.Count);

    /// <summary>Se dispara (en un hilo de fondo) cuando <see cref="StartWatching"/> aplica cambios al indice.</summary>
    public event EventHandler<IndexChange>? Changed;

    internal FileIndex(IReadOnlyList<VolumeIndex> volumes, IReadOnlyDictionary<char, Exception> failures)
    {
        Volumes = volumes;
        Failures = failures;
    }

    public static IReadOnlyList<char> GetNtfsDrives() => DriveInfo.GetDrives()
        .Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.DriveFormat == "NTFS")
        .Select(d => d.Name[0])
        .ToArray();

    public static FileIndex Build(IEnumerable<char> drives)
    {
        var letters = drives.Select(char.ToUpperInvariant).Distinct().ToArray();
        var volumes = new VolumeIndex?[letters.Length];
        var failures = new ConcurrentDictionary<char, Exception>();

        Parallel.For(0, letters.Length, i =>
        {
            try
            {
                volumes[i] = VolumeIndex.Build(letters[i]);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception or IOException)
            {
                failures[letters[i]] = ex;
            }
        });

        // La construccion deja mucha memoria temporal (buffers, listas que crecieron, nombres repetidos):
        // una compactacion completa la devuelve al sistema de una vez
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        return new FileIndex(volumes.OfType<VolumeIndex>().ToArray(), failures);
    }

    /// <summary>Aplica los cambios del USN Journal de todas las unidades e indica que cambio.</summary>
    public IndexChange Update()
    {
        var change = IndexChange.None;
        foreach (var volume in Volumes)
        {
            try
            {
                change |= volume.Update();
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                // La unidad pudo desmontarse; se reintenta en la siguiente vuelta
            }
        }
        return change;
    }

    /// <summary>
    /// Lee de la $MFT el tamano y la fecha de todas las entradas de todas las unidades (ver
    /// <see cref="VolumeIndex.LoadDetailsFromMft"/>). Si una unidad falla, sus datos se siguen pidiendo
    /// archivo por archivo cuando hagan falta. Devuelve cuantas entradas se completaron.
    /// </summary>
    public int LoadDetailsFromMft(CancellationToken cancellationToken = default)
    {
        int filled = 0;
        foreach (var volume in Volumes)
        {
            try
            {
                filled += volume.LoadDetailsFromMft(cancellationToken);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }
        }
        return filled;
    }

    /// <summary>Revisa el USN Journal cada <paramref name="interval"/> y dispara <see cref="Changed"/> si hubo cambios.</summary>
    public void StartWatching(TimeSpan interval)
    {
        if (_watchCts is not null) return;
        var cts = _watchCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    var change = Update();
                    if (change != IndexChange.None) Changed?.Invoke(this, change);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
            }
        });
    }

    /// <summary>Prepara una busqueda limitada a <paramref name="rootPath"/> y sus subcarpetas.</summary>
    /// <exception cref="DirectoryNotFoundException">La unidad no esta indexada o la carpeta no existe en el indice.</exception>
    public SearchScope CreateScope(string rootPath)
    {
        var full = Path.GetFullPath(rootPath.Trim());
        if (full.Length < 3 || full[1] != ':')
            throw new DirectoryNotFoundException($"Solo se admiten carpetas de unidades locales: {rootPath}");

        var volume = Volumes.FirstOrDefault(v => v.Drive == char.ToUpperInvariant(full[0]))
            ?? throw new DirectoryNotFoundException($"La unidad {char.ToUpperInvariant(full[0])}: no esta indexada.");

        return volume.CreateScope(full[3..].TrimEnd('\\'));
    }

    /// <summary>
    /// Busca por nombre, sin distinguir mayusculas. El texto admite varias palabras, frases entre comillas,
    /// comodines y exclusiones (ver <see cref="SearchQuery"/>). Texto vacio devuelve todo.
    /// </summary>
    public List<SearchResult> Search(string text, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SearchOptions();
        var scope = options.Scope;
        var query = SearchQuery.Parse(text);

        var results = new List<SearchResult>();
        foreach (var volume in Volumes)
        {
            if (scope is null || scope.Volume == volume)
                volume.Search(query, options.Kind, scope, options.IncludeSystem, results, cancellationToken);
        }

        if (options.ModifiedFrom is not null || options.ModifiedTo is not null || options.MinSize is not null || options.MaxSize is not null)
            results = FilterByDetails(results, options, cancellationToken);
        return results;
    }

    /// <summary>
    /// Deja las entradas cuya fecha esta en [ModifiedFrom, ModifiedTo) y cuyo tamano esta en [MinSize, MaxSize).
    /// Con filtro de tamano se excluyen las carpetas. Los datos salen de la cache (cargada desde la $MFT) o
    /// se consultan en paralelo.
    /// </summary>
    static List<SearchResult> FilterByDetails(List<SearchResult> results, SearchOptions options, CancellationToken cancellationToken)
    {
        var (from, to) = (options.ModifiedFrom, options.ModifiedTo);
        var (minSize, maxSize) = (options.MinSize, options.MaxSize);
        bool byDate = from is not null || to is not null;
        bool bySize = minSize is not null || maxSize is not null;

        var keep = new bool[results.Count];
        Parallel.For(0, results.Count, new ParallelOptions { CancellationToken = cancellationToken }, i =>
        {
            var details = results[i].Details;
            keep[i] = (!byDate || details.Modified is { } m && (from is null || m >= from) && (to is null || m < to)) &&
                      (!bySize || details.Size is { } s && (minSize is null || s >= minSize) && (maxSize is null || s < maxSize));
        });

        var filtered = new List<SearchResult>();
        for (int i = 0; i < keep.Length; i++)
            if (keep[i]) filtered.Add(results[i]);
        return filtered;
    }

    public void Dispose()
    {
        _watchCts?.Cancel();
        foreach (var volume in Volumes)
            volume.Dispose();
    }
}
