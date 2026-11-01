namespace EasySearcher.Core;

/// <summary>Referencia liviana a una entrada del indice; la ruta se arma solo cuando se pide.</summary>
public readonly record struct SearchResult(VolumeIndex Volume, int Entry)
{
    public string Name => Volume.GetName(Entry);
    public bool IsDirectory => Volume.IsDirectory(Entry);
    public FileAttributes Attributes => Volume.GetAttributes(Entry);
    public string FullPath => Volume.GetFullPath(Entry);
    public string DirectoryPath => Volume.GetDirectoryPath(Entry);

    /// <summary>Tamano y fecha; la primera lectura consulta el disco.</summary>
    public FileDetails Details => Volume.GetDetails(Entry);
    public long? Size => Details.Size;
    public DateTime? Modified => Details.Modified;
}
