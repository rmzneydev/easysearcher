using System.IO;
using System.Text.Json;
using EasySearcher.Core;

namespace EasySearcher.App;

/// <summary>Vistas de la lista de resultados, en el mismo orden que el ComboBox de la ventana.</summary>
enum ViewMode
{
    Details,
    MediumIcons,
    LargeIcons,
}

/// <summary>Preferencias del usuario en %APPDATA%\EasySearcher\settings.json.</summary>
sealed class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasySearcher", "settings.json");

    /// <summary>Carpeta raiz de las busquedas; null = todas las unidades.</summary>
    public string? RootPath { get; set; }

    /// <summary>Mostrar archivos ocultos + sistema.</summary>
    public bool IncludeSystem { get; set; }

    public SortColumn SortColumn { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }

    public ViewMode ViewMode { get; set; } = ViewMode.Details;

    /// <summary>Guardar las busquedas que se usan (ver SearchHistory).</summary>
    public bool HistoryEnabled { get; set; } = true;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
