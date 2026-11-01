using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace EasySearcher.Core.Service;

/// <summary>
/// Cliente del servicio de Windows. Mantiene varias conexiones para que una busqueda larga (p. ej. ordenar
/// por tamano) no bloquee la carga de filas mientras se hace scroll.
/// </summary>
public sealed class PipeSearchClient : ISearchService
{
    const int ConnectTimeoutMs = 1000;

    readonly ConcurrentBag<NamedPipeClientStream> _idle = [];

    PipeSearchClient()
    {
    }

    /// <summary>Se conecta al servicio y comprueba que responde.</summary>
    /// <exception cref="SearchServiceException">El servicio no esta en ejecucion.</exception>
    public static PipeSearchClient Connect()
    {
        var client = new PipeSearchClient();
        try
        {
            client.GetStatus();
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return client;
    }

    public EngineStatus GetStatus() =>
        Send(new PipeRequest { Op = PipeOps.Status }).Status ?? throw Invalid();

    public string ResolveRoot(string root) =>
        Send(new PipeRequest { Op = PipeOps.Root, Path = root }).Path ?? throw Invalid();

    public SearchResponse Search(SearchRequest request) =>
        Send(new PipeRequest { Op = PipeOps.Search, Search = request }).Search ?? throw Invalid();

    public RowData[] GetRows(int searchId, int start, int count) =>
        Send(new PipeRequest { Op = PipeOps.Rows, SearchId = searchId, Start = start, Count = count }).Rows ?? [];

    public RowDetails[] GetDetails(int searchId, int start, int count) =>
        Send(new PipeRequest { Op = PipeOps.Details, SearchId = searchId, Start = start, Count = count }).Details ?? [];

    PipeResponse Send(PipeRequest request)
    {
        NamedPipeClientStream? pipe = null;
        PipeResponse? response;
        try
        {
            pipe = Rent();
            PipeProtocol.Write(pipe, request);
            response = PipeProtocol.Read<PipeResponse>(pipe) ?? throw new EndOfStreamException();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException
                                       or JsonException or InvalidDataException)
        {
            pipe?.Dispose();
            throw new SearchServiceException("El servicio de EasySearcher no esta disponible.");
        }

        _idle.Add(pipe);
        if (response.Error is not null) throw new SearchServiceException(response.Error);
        return response;
    }

    NamedPipeClientStream Rent()
    {
        while (_idle.TryTake(out var idle))
        {
            if (idle.IsConnected) return idle;
            idle.Dispose();
        }

        var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut);
        try
        {
            pipe.Connect(ConnectTimeoutMs);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
        return pipe;
    }

    static SearchServiceException Invalid() => new("Respuesta invalida del servicio de EasySearcher.");

    public void Dispose()
    {
        while (_idle.TryTake(out var pipe))
            pipe.Dispose();
    }
}
