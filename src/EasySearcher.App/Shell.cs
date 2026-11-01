using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace EasySearcher.App;

/// <summary>
/// Abre archivos y carpetas a traves de explorer.exe. Como la app corre como administrador,
/// explorer.exe delega en la instancia del escritorio del usuario y lo que se abre no hereda
/// los permisos elevados.
/// </summary>
static class Shell
{
    public static void Open(string path) => Start($"\"{path}\"");

    public static void ShowInFolder(string path) => Start($"/select,\"{path}\"");

    static void Start(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(ex.Message, "EasySearcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
