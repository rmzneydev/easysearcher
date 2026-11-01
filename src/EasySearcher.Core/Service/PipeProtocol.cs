using System.Buffers.Binary;
using System.Text.Json;

namespace EasySearcher.Core.Service;

/// <summary>
/// Mensajes entre la app y el servicio: cada uno es un entero de 4 bytes con la longitud seguido de JSON.
/// Una conexion atiende una peticion a la vez (peticion, respuesta, peticion...).
/// </summary>
internal static class PipeProtocol
{
    public const string PipeName = "EasySearcher.v1";

    const int MaxMessageSize = 64 * 1024 * 1024;

    public static void Write<T>(Stream stream, T message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    /// <summary>Devuelve null si el otro extremo cerro la conexion.</summary>
    public static T? Read<T>(Stream stream) where T : class
    {
        Span<byte> header = stackalloc byte[4];
        int read = stream.ReadAtLeast(header, 4, throwOnEndOfStream: false);
        if (read == 0) return null;
        if (read < 4) throw new EndOfStreamException();

        var payload = new byte[CheckLength(BinaryPrimitives.ReadInt32LittleEndian(header))];
        stream.ReadExactly(payload);
        return JsonSerializer.Deserialize<T>(payload);
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>Devuelve null si el otro extremo cerro la conexion.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken) where T : class
    {
        var header = new byte[4];
        int read = await stream.ReadAtLeastAsync(header, 4, throwOnEndOfStream: false, cancellationToken);
        if (read == 0) return null;
        if (read < 4) throw new EndOfStreamException();

        var payload = new byte[CheckLength(BinaryPrimitives.ReadInt32LittleEndian(header))];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload);
    }

    static int CheckLength(int length) => length is >= 0 and <= MaxMessageSize
        ? length
        : throw new InvalidDataException($"Mensaje de tamano invalido: {length}");
}

internal static class PipeOps
{
    public const string Status = "status";
    public const string Root = "root";
    public const string Search = "search";
    public const string Rows = "rows";
    public const string Details = "details";
}

internal sealed class PipeRequest
{
    public string Op { get; set; } = "";
    public string? Path { get; set; }
    public SearchRequest? Search { get; set; }
    public int SearchId { get; set; }
    public int Start { get; set; }
    public int Count { get; set; }
}

internal sealed class PipeResponse
{
    public string? Error { get; set; }
    public EngineStatus? Status { get; set; }
    public string? Path { get; set; }
    public SearchResponse? Search { get; set; }
    public RowData[]? Rows { get; set; }
    public RowDetails[]? Details { get; set; }
}
