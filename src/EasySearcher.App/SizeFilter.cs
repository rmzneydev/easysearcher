namespace EasySearcher.App;

/// <summary>Categorias de tamano del Explorador, en el mismo orden que el ComboBox de la ventana.</summary>
enum SizePreset
{
    Any,
    Empty,
    Tiny,
    Small,
    Medium,
    Large,
    Huge,
    Gigantic,
}

static class SizeFilter
{
    const long KB = 1024, MB = 1024 * KB, GB = 1024 * MB;

    /// <summary>Rango [minimo, maximo) en bytes; null = sin limite.</summary>
    public static (long? Min, long? Max) GetRange(SizePreset preset) => preset switch
    {
        SizePreset.Empty => (0, 1),
        SizePreset.Tiny => (1, 16 * KB),
        SizePreset.Small => (16 * KB, 1 * MB),
        SizePreset.Medium => (1 * MB, 128 * MB),
        SizePreset.Large => (128 * MB, 1 * GB),
        SizePreset.Huge => (1 * GB, 4 * GB),
        SizePreset.Gigantic => (4 * GB, null),
        _ => (null, null),
    };

    /// <summary>Texto para la barra de estado.</summary>
    public static string Describe(SizePreset preset) => preset switch
    {
        SizePreset.Empty => "vacios",
        SizePreset.Tiny => "de menos de 16 KB",
        SizePreset.Small => "de 16 KB a 1 MB",
        SizePreset.Medium => "de 1 a 128 MB",
        SizePreset.Large => "de 128 MB a 1 GB",
        SizePreset.Huge => "de 1 a 4 GB",
        SizePreset.Gigantic => "de mas de 4 GB",
        _ => "",
    };
}
