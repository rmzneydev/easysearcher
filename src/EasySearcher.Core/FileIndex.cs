using System.Collections.Concurrent;
using System.ComponentModel;

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
    public event EventHandler? Changed;

    FileIndex(IReadOnlyList<VolumeIndex> volumes, IReadOnlyDictionary<char, Exception> failures)
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

        return new FileIndex(volumes.OfType<VolumeIndex>().ToArray(), failures);
    }

    /// <summary>Aplica los cambios del USN Journal de todas las unidades. Devuelve true si algo cambio.</summary>
    public bool Update()
    {
        bool changed = false;
        foreach (var volume in Volumes)
        {
            try
            {
                changed |= volume.Update();
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                // La unidad pudo desmontarse; se reintenta en la siguiente vuelta
            }
        }
        return changed;
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
                    if (Update()) Changed?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Busca por subcadena en el nombre, sin distinguir mayusculas. Texto vacio devuelve todo.</summary>
    public List<SearchResult> Search(string text, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SearchOptions();
        var scope = options.Scope;

        var results = new List<SearchResult>();
        foreach (var volume in Volumes)
        {
            if (scope is null || scope.Volume == volume)
                volume.Search(text, options.Kind, scope, options.IncludeSystem, results, cancellationToken);
        }
        return results;
    }

    public void Dispose()
    {
        _watchCts?.Cancel();
        foreach (var volume in Volumes)
            volume.Dispose();
    }
}
