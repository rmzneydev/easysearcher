namespace EasySearcher.Core.Service;

/// <summary>
/// Operaciones que la interfaz necesita del motor de busqueda. La implementan <see cref="SearchEngine"/>
/// (en el mismo proceso) y <see cref="PipeSearchClient"/> (hablando con el servicio de Windows).
/// Los errores que se deben mostrar al usuario llegan como <see cref="SearchServiceException"/>.
/// </summary>
public interface ISearchService : IDisposable
{
    EngineStatus GetStatus();

    /// <summary>Valida una carpeta raiz y devuelve su ruta tal como esta en disco.</summary>
    string ResolveRoot(string root);

    SearchResponse Search(SearchRequest request);

    /// <summary>Filas de una busqueda. Vacio si la busqueda ya no esta guardada (hay que repetirla).</summary>
    RowData[] GetRows(int searchId, int start, int count);

    /// <summary>Tamano y fecha de las filas indicadas; puede consultar el disco.</summary>
    RowDetails[] GetDetails(int searchId, int start, int count);
}

public sealed class SearchServiceException(string message) : Exception(message);

public static class Protocol
{
    /// <summary>
    /// Version de lo que entiende el motor. Un servicio instalado mas viejo ignora los campos nuevos sin
    /// avisar, asi que la app la compara antes de usarlos.
    /// 1 = 0.1.0; 2 = filtro por fecha de modificacion; 3 = categorias de EntryKind (imagenes, videos...);
    /// 4 = filtro por tamano, DetailsCounter y DetailsReady.
    /// </summary>
    public const int Version = 4;
}

/// <param name="Ready">False mientras se construye el indice.</param>
/// <param name="ChangeCounter">Aumenta cuando cambia la estructura del indice (altas, bajas, renombres, movimientos).</param>
/// <param name="ProtocolVersion">Ver <see cref="Protocol.Version"/>; un servicio 0.1.0 no lo envia (queda en 1).</param>
/// <param name="DetailsCounter">Aumenta cuando cambian tamanos o fechas (contenido de archivos) o termina de leerse la $MFT.</param>
/// <param name="DetailsReady">True cuando ya se leyeron de la $MFT el tamano y la fecha de todo.</param>
public sealed record EngineStatus(
    bool Ready,
    int Entries,
    long ChangeCounter,
    string[] Drives,
    string[] NotLive,
    string[] Failures,
    double BuildSeconds,
    int ProtocolVersion = 1,
    long DetailsCounter = 0,
    bool DetailsReady = false);

/// <param name="Root">Carpeta raiz; null o vacia = todas las unidades.</param>
/// <param name="PreviousSearchId">Si se indica y el resultado es identico, se reutiliza esa busqueda.</param>
/// <param name="ModifiedFrom">Modificados desde (incluido), hora local. Protocolo 2.</param>
/// <param name="ModifiedTo">Modificados antes de (excluido), hora local. Protocolo 2.</param>
/// <param name="MinSize">Archivos de al menos estos bytes (excluye carpetas). Protocolo 4.</param>
/// <param name="MaxSize">Archivos de menos de estos bytes (excluye carpetas). Protocolo 4.</param>
public sealed record SearchRequest(
    string Text,
    EntryKind Kind,
    string? Root,
    bool IncludeSystem,
    SortColumn Sort,
    bool Descending,
    int PreviousSearchId = 0,
    DateTime? ModifiedFrom = null,
    DateTime? ModifiedTo = null,
    long? MinSize = null,
    long? MaxSize = null);

/// <param name="Unchanged">True si el resultado es identico al de PreviousSearchId.</param>
public sealed record SearchResponse(int SearchId, int Total, string? Root, bool Unchanged, double ElapsedMs);

public sealed record RowData(char Drive, int Entry, string Name, string Directory, bool IsDirectory);

public sealed record RowDetails(long? Size, DateTime? Modified);
