using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasySearcher.App;

/// <summary>Opciones del filtro de tamano, en el mismo orden que el ComboBox de la ventana.</summary>
enum SizeMode
{
    Any,
    Larger,
    Smaller,
    Empty,
}

/// <summary>Unidades de la cifra, en el mismo orden que el ComboBox de la ventana.</summary>
enum SizeUnit
{
    KB,
    MB,
    GB,
}

/// <summary>Filtro de tamano; el valor por defecto no filtra. Con Any y Empty la cifra y la unidad no cuentan.</summary>
[JsonConverter(typeof(SizeCriterionConverter))]
readonly record struct SizeCriterion(SizeMode Mode, double Value = 0, SizeUnit Unit = SizeUnit.KB);

static class SizeFilter
{
    const long KB = 1024, MB = 1024 * KB, GB = 1024 * MB;

    // Mas que cualquier volumen, y lejos de desbordar un long al pasar a bytes
    const double MaxValue = 1e9;

    /// <summary>
    /// Criterio a partir de lo que hay en la ventana. Sin una cifra valida no se filtra: es el estado
    /// normal justo despues de elegir "Mayor que", antes de escribirla.
    /// </summary>
    public static SizeCriterion Create(SizeMode mode, string text, SizeUnit unit) => mode switch
    {
        SizeMode.Empty => new SizeCriterion(SizeMode.Empty),
        SizeMode.Larger or SizeMode.Smaller when ParseValue(text) is { } value => new SizeCriterion(mode, value, unit),
        _ => default,
    };

    /// <summary>Acepta coma o punto decimal, sea cual sea la configuracion regional.</summary>
    public static double? ParseValue(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)
        && value is >= 0 and < MaxValue
            ? value
            : null;

    /// <summary>La cifra tal como se muestra en la ventana y en los textos.</summary>
    public static string FormatValue(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    /// <summary>Rango [minimo, maximo) en bytes; null = sin limite.</summary>
    public static (long? Min, long? Max) GetRange(SizeCriterion size) => size.Mode switch
    {
        SizeMode.Larger => (ToBytes(size) + 1, null),   // "mayor que" es estricto y el minimo, inclusivo
        SizeMode.Smaller => (null, ToBytes(size)),
        SizeMode.Empty => (0, 1),
        _ => (null, null),
    };

    /// <summary>Texto para la barra de estado y el historial.</summary>
    public static string Describe(SizeCriterion size) => size.Mode switch
    {
        SizeMode.Larger => $"de mas de {FormatValue(size.Value)} {size.Unit}",
        SizeMode.Smaller => $"de menos de {FormatValue(size.Value)} {size.Unit}",
        SizeMode.Empty => "vacios",
        _ => "",
    };

    static long ToBytes(SizeCriterion size) => (long)(size.Value * size.Unit switch
    {
        SizeUnit.GB => GB,
        SizeUnit.MB => MB,
        _ => KB,
    });
}

/// <summary>
/// El historial guardaba antes un numero de categoria (0 = cualquiera ... 7 = gigantesco). Esas entradas se
/// leen como el limite equivalente: un criterio solo tiene un limite, asi que los rangos cerrados se abren.
/// </summary>
sealed class SizeCriterionConverter : JsonConverter<SizeCriterion>
{
    internal sealed record Stored(SizeMode Mode, double Value, SizeUnit Unit);

    public override SizeCriterion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number) return FromCategory(reader.GetInt32());
        var stored = JsonSerializer.Deserialize<Stored>(ref reader, options);
        return stored is null ? default : new SizeCriterion(stored.Mode, stored.Value, stored.Unit);
    }

    public override void Write(Utf8JsonWriter writer, SizeCriterion value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Stored(value.Mode, value.Value, value.Unit), options);

    static SizeCriterion FromCategory(int category) => category switch
    {
        1 => new SizeCriterion(SizeMode.Empty),
        2 => new SizeCriterion(SizeMode.Smaller, 16, SizeUnit.KB),
        3 => new SizeCriterion(SizeMode.Smaller, 1, SizeUnit.MB),
        4 => new SizeCriterion(SizeMode.Smaller, 128, SizeUnit.MB),
        5 => new SizeCriterion(SizeMode.Larger, 128, SizeUnit.MB),
        6 => new SizeCriterion(SizeMode.Larger, 1, SizeUnit.GB),
        7 => new SizeCriterion(SizeMode.Larger, 4, SizeUnit.GB),
        _ => default,
    };
}
