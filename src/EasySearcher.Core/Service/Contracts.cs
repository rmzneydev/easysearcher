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

/// <param name="Ready">False mientras se construye el indice.</param>
/// <param name="ChangeCounter">Aumenta cada vez que el indice cambia por actividad en disco.</param>
public sealed record EngineStatus(
    bool Ready,
    int Entries,
    long ChangeCounter,
    string[] Drives,
    string[] NotLive,
    string[] Failures,
    double BuildSeconds);

/// <param name="Root">Carpeta raiz; null o vacia = todas las unidades.</param>
/// <param name="PreviousSearchId">Si se indica y el resultado es identico, se reutiliza esa busqueda.</param>
public sealed record SearchRequest(
    string Text,
    EntryKind Kind,
    string? Root,
    bool IncludeSystem,
    SortColumn Sort,
    bool Descending,
    int PreviousSearchId = 0);

/// <param name="Unchanged">True si el resultado es identico al de PreviousSearchId.</param>
public sealed record SearchResponse(int SearchId, int Total, string? Root, bool Unchanged, double ElapsedMs);

public sealed record RowData(char Drive, int Entry, string Name, string Directory, bool IsDirectory);

public sealed record RowDetails(long? Size, DateTime? Modified);
