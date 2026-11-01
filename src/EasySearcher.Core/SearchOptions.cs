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
}
