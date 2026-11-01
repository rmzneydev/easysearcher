namespace EasySearcher.Core;

/// <summary>Referencia liviana a una entrada del indice; la ruta se arma solo cuando se pide.</summary>
public readonly record struct SearchResult(VolumeIndex Volume, int Entry)
{
    public string Name => Volume.GetName(Entry);
    public bool IsDirectory => Volume.IsDirectory(Entry);
    public FileAttributes Attributes => Volume.GetAttributes(Entry);
    public string FullPath => Volume.GetFullPath(Entry);
}
