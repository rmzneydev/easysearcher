namespace EasySearcher.Core;

public sealed record SearchOptions
{
    public EntryKind Kind { get; init; } = EntryKind.All;

    /// <summary>Carpeta a la que se limita la busqueda; null = todas las unidades.</summary>
    public SearchScope? Scope { get; init; }

    /// <summary>
    /// Incluir lo oculto + sistema (System Volume Information, $Recycle.Bin, desktop.ini...).
    /// Se activa solo si la carpeta raiz ya es de sistema.
    /// </summary>
    public bool IncludeSystem { get; init; }

    /// <summary>
    /// Solo entradas modificadas desde esta fecha (incluida), en hora local. La fecha no esta en el indice:
    /// se consulta al disco para cada coincidencia del texto (con cache).
    /// </summary>
    public DateTime? ModifiedFrom { get; init; }

    /// <summary>Solo entradas modificadas antes de esta fecha (excluida), en hora local.</summary>
    public DateTime? ModifiedTo { get; init; }

    /// <summary>Solo archivos de al menos estos bytes. Con filtro de tamano no aparecen carpetas.</summary>
    public long? MinSize { get; init; }

    /// <summary>Solo archivos de menos de estos bytes.</summary>
    public long? MaxSize { get; init; }
}
