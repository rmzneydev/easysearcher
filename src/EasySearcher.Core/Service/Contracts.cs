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
    /// 1 = 0.1.0; 2 = filtro por fecha de modificacion; 3 = categorias de EntryKind (imagenes, videos...).
    /// </summary>
    public const int Version = 3;
}

/// <param name="Ready">False mientras se construye el indice.</param>
/// <param name="ChangeCounter">Aumenta cada vez que el indice cambia por actividad en disco.</param>
/// <param name="ProtocolVersion">Ver <see cref="Protocol.Version"/>; un servicio 0.1.0 no lo envia (queda en 1).</param>
public sealed record EngineStatus(
    bool Ready,
    int Entries,
    long ChangeCounter,
    string[] Drives,
    string[] NotLive,
    string[] Failures,
    double BuildSeconds,
    int ProtocolVersion = 1);

/// <param name="Root">Carpeta raiz; null o vacia = todas las unidades.</param>
/// <param name="PreviousSearchId">Si se indica y el resultado es identico, se reutiliza esa busqueda.</param>
/// <param name="ModifiedFrom">Modificados desde (incluido), hora local. Protocolo 2.</param>
/// <param name="ModifiedTo">Modificados antes de (excluido), hora local. Protocolo 2.</param>
public sealed record SearchRequest(
    string Text,
    EntryKind Kind,
    string? Root,
    bool IncludeSystem,
    SortColumn Sort,
    bool Descending,
    int PreviousSearchId = 0,
    DateTime? ModifiedFrom = null,
    DateTime? ModifiedTo = null);

/// <param name="Unchanged">True si el resultado es identico al de PreviousSearchId.</param>
public sealed record SearchResponse(int SearchId, int Total, string? Root, bool Unchanged, double ElapsedMs);

public sealed record RowData(char Drive, int Entry, string Name, string Directory, bool IsDirectory);

public sealed record RowDetails(long? Size, DateTime? Modified);
