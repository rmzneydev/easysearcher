using System.Windows;

namespace EasySearcher.App;

public partial class App : Application
{
    SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = StartupArgs.Parse(e.Args);

        _instance = SingleInstance.TryAcquire();
        if (_instance is null && SingleInstance.TrySendToExisting(args))
        {
            Shutdown();
            return;
        }
        // Si la otra instancia no respondio, se abre igual una ventana nueva

        var window = new MainWindow(args);
        MainWindow = window;
        window.Show();

        _instance?.Listen(received => Dispatcher.InvokeAsync(() => window.HandleArgsAsync(received)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
