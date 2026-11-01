using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace EasySearcher.App;

/// <summary>
/// Garantiza una sola ventana por usuario: la primera instancia escucha en un named pipe y las
/// siguientes le mandan sus argumentos y se cierran. Mutex y pipe llevan el SID del usuario para
/// que dos usuarios con sesion abierta no se mezclen.
/// </summary>
sealed class SingleInstance : IDisposable
{
    const int ConnectTimeoutMs = 2000;

    static readonly string Id = $"EasySearcher.App.{WindowsIdentity.GetCurrent().User?.Value}";
    static string PipeName => Id;

    readonly Mutex _mutex;
    readonly CancellationTokenSource _cts = new();

    SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Devuelve null si ya hay otra instancia abierta.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{Id}", out bool createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>Manda los argumentos a la instancia abierta y le permite pasar al frente.</summary>
    public static bool TrySendToExisting(StartupArgs args)
    {
        try
        {
            // CurrentUserOnly: solo se conecta si el pipe pertenece a este mismo usuario
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMs);

            // Windows solo deja traer una ventana al frente si lo autoriza el proceso que tiene el foco
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid))
                AllowSetForegroundWindow(pid);

            using var writer = new StreamWriter(pipe);
            writer.WriteLine(JsonSerializer.Serialize(args));
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Atiende a las instancias nuevas; <paramref name="onArgs"/> se llama en un hilo de fondo.</summary>
    public void Listen(Action<StartupArgs> onArgs)
    {
        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(pipe);
                    var line = await reader.ReadLineAsync(token);
                    if (line is not null && JsonSerializer.Deserialize<StartupArgs>(line) is { } args)
                        onArgs(args);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // Mensaje roto de otra instancia: se ignora y se sigue escuchando
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AllowSetForegroundWindow(uint processId);
}
