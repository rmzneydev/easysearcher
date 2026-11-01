using System.Collections.Concurrent;
using System.ComponentModel;

namespace EasySearcher.Core;

/// <summary>Indice de varias unidades NTFS; cada una se lee en paralelo.</summary>
public sealed class FileIndex
{
    public IReadOnlyList<VolumeIndex> Volumes { get; }

    /// <summary>Unidades que no se pudieron indexar y el motivo.</summary>
    public IReadOnlyDictionary<char, Exception> Failures { get; }

    public int Count => Volumes.Sum(v => v.Count);

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

    /// <summary>Prepara una busqueda limitada a <paramref name="rootPath"/> y sus subcarpetas.</summary>
    /// <exception cref="DirectoryNotFoundException">La unidad no esta indexada o la carpeta no existe en el indice.</exception>
    public SearchScope CreateScope(string rootPath)
    {
        var full = Path.GetFullPath(rootPath.Trim());
        if (full.Length < 3 || full[1] != ':')
            throw new DirectoryNotFoundException($"Solo se admiten carpetas de unidades locales: {rootPath}");

        var volume = Volumes.FirstOrDefault(v => v.Drive == char.ToUpperInvariant(full[0]))
            ?? throw new DirectoryNotFoundException($"La unidad {char.ToUpperInvariant(full[0])}: no esta indexada.");

        var relative = full[3..].TrimEnd('\\');
        if (relative.Length == 0)
            return new SearchScope($@"{volume.Drive}:\", volume, null, false);

        int entry = volume.FindDirectory(relative);
        if (entry < 0)
            throw new DirectoryNotFoundException($"No existe la carpeta: {full}");

        return new SearchScope(volume.GetFullPath(entry), volume, volume.GetSubtreeMask(entry), volume.IsSystem(entry));
    }

    /// <summary>Busca por subcadena en el nombre, sin distinguir mayusculas. Texto vacio devuelve todo.</summary>
    public List<SearchResult> Search(string text, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SearchOptions();
        var scope = options.Scope;
        bool includeSystem = options.IncludeSystem || scope?.IsSystem == true;

        var results = new List<SearchResult>();
        if (scope is not null)
        {
            scope.Volume.Search(text, options.Kind, scope.Mask, includeSystem, results, cancellationToken);
            return results;
        }
        foreach (var volume in Volumes)
            volume.Search(text, options.Kind, null, includeSystem, results, cancellationToken);
        return results;
    }
}
