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

    /// <summary>Busca por subcadena en el nombre, sin distinguir mayusculas. Texto vacio devuelve todo.</summary>
    public List<SearchResult> Search(string text, EntryKind kind = EntryKind.All, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResult>();
        foreach (var volume in Volumes)
            volume.Search(text, kind, results, cancellationToken);
        return results;
    }
}
