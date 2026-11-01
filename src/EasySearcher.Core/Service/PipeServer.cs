using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace EasySearcher.Core.Service;

/// <summary>Atiende las peticiones de la app por un named pipe, una tarea por conexion.</summary>
public static class PipeServer
{
    public static Task RunAsync(ISearchService service, CancellationToken cancellationToken) =>
        RunAsync(service, PipeProtocol.PipeName, cancellationToken);

    /// <summary>Con otro nombre de pipe, para que las pruebas no choquen con el servicio instalado.</summary>
    internal static async Task RunAsync(ISearchService service, string pipeName, CancellationToken cancellationToken)
    {
        var security = CreateSecurity();
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            _ = Task.Run(() => HandleConnectionAsync(pipe, service, cancellationToken), cancellationToken);
        }
    }

    /// <summary>
    /// Los usuarios del equipo pueden leer y escribir, pero no crear instancias del pipe:
    /// asi otro proceso no puede hacerse pasar por el servicio mientras este corre.
    /// El usuario del propio proceso (SYSTEM en el servicio) necesita control total para crearlas.
    /// </summary>
    static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    static async Task HandleConnectionAsync(NamedPipeServerStream pipe, ISearchService service, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                while (await PipeProtocol.ReadAsync<PipeRequest>(pipe, cancellationToken) is { } request)
                    await PipeProtocol.WriteAsync(pipe, Dispatch(service, request), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException)
            {
                // El cliente se fue o mando algo invalido: se cierra esta conexion
            }
        }
    }

    static PipeResponse Dispatch(ISearchService service, PipeRequest request)
    {
        try
        {
            return request.Op switch
            {
                PipeOps.Status => new PipeResponse { Status = service.GetStatus() },
                PipeOps.Root => new PipeResponse { Path = service.ResolveRoot(request.Path ?? "") },
                PipeOps.Search when request.Search is not null => new PipeResponse { Search = service.Search(request.Search) },
                PipeOps.Rows => new PipeResponse { Rows = service.GetRows(request.SearchId, request.Start, request.Count) },
                PipeOps.Details => new PipeResponse { Details = service.GetDetails(request.SearchId, request.Start, request.Count) },
                _ => new PipeResponse { Error = $"Operacion desconocida: {request.Op}" },
            };
        }
        catch (SearchServiceException ex)
        {
            return new PipeResponse { Error = ex.Message };
        }
    }
}
