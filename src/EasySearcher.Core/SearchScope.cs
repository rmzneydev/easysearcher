namespace EasySearcher.Core;

/// <summary>Limita la busqueda a una carpeta y todo lo que contiene. Se crea con <see cref="FileIndex.CreateScope"/>.</summary>
public sealed class SearchScope
{
    public string RootPath { get; }

    internal VolumeIndex Volume { get; }

    /// <summary>Ruta de la carpeta sin la letra de unidad; vacia = todo el volumen.</summary>
    internal string RelativePath { get; }

    /// <summary>
    /// Que entradas estan dentro de la carpeta, para la version del indice indicada.
    /// Se reemplaza entera (nunca se modifica) para que las busquedas concurrentes la lean sin lock.
    /// </summary>
    internal MaskCache? Cache { get; set; }

    internal sealed record MaskCache(int Generation, int Version, int RootEntry, bool[] Mask);

    internal SearchScope(string rootPath, VolumeIndex volume, string relativePath)
    {
        RootPath = rootPath;
        Volume = volume;
        RelativePath = relativePath;
    }
}
