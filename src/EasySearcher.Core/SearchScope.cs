namespace EasySearcher.Core;

/// <summary>Limita la busqueda a una carpeta y todo lo que contiene. Se crea con <see cref="FileIndex.CreateScope"/>.</summary>
public sealed class SearchScope
{
    public string RootPath { get; }

    internal VolumeIndex Volume { get; }

    /// <summary>Entradas dentro de la carpeta; null cuando la raiz es la unidad completa.</summary>
    internal bool[]? Mask { get; }

    /// <summary>La raiz esta dentro de una carpeta oculta + sistema, asi que su contenido se muestra igual.</summary>
    internal bool IsSystem { get; }

    internal SearchScope(string rootPath, VolumeIndex volume, bool[]? mask, bool isSystem)
    {
        RootPath = rootPath;
        Volume = volume;
        Mask = mask;
        IsSystem = isSystem;
    }
}
