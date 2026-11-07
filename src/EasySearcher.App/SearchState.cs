using EasySearcher.Core;

namespace EasySearcher.App;

/// <summary>
/// Una busqueda con todo su contexto: la que hay en la ventana y las guardadas en el historial, donde
/// ademas cuenta <see cref="LastUsed"/>. Las fechas relativas ("esta semana") se guardan como opcion, no
/// como fechas fijas.
/// </summary>
sealed record SearchState(
    string Text,
    string? Root,
    EntryKind Kind,
    DatePreset Date,
    DateTime? CustomFrom,
    DateTime? CustomTo,
    SizeCriterion Size)
{
    public DateTime LastUsed { get; init; }

    /// <summary>Hay algun filtro puesto; el texto y la carpeta no son filtros.</summary>
    public bool HasFilters => Kind != EntryKind.All || UsesDetails;

    /// <summary>El resultado depende de tamanos o fechas.</summary>
    public bool UsesDetails => Date != DatePreset.Any || Size != default;

    public bool SameContext(SearchState other) =>
        string.Equals(Root, other.Root, StringComparison.OrdinalIgnoreCase) &&
        Kind == other.Kind && Date == other.Date && Size == other.Size &&
        CustomFrom == other.CustomFrom && CustomTo == other.CustomTo;

    public bool SameSearch(SearchState other) =>
        SameContext(other) && string.Equals(Text.Trim(), other.Text.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Contexto para mostrar, p. ej. "en D:\Musica · imágenes · este mes".</summary>
    public string Description
    {
        get
        {
            var parts = new List<string> { "en " + (Root ?? "todas las unidades") };
            string kind = Kind switch
            {
                EntryKind.Files => "archivos",
                EntryKind.Directories => "carpetas",
                EntryKind.Images => "imágenes",
                EntryKind.Videos => "videos",
                EntryKind.Audio => "música",
                EntryKind.Documents => "documentos",
                _ => "",
            };
            foreach (var part in new[] { kind, DateFilter.Describe(Date, CustomFrom, CustomTo), SizeFilter.Describe(Size) })
                if (part.Length > 0) parts.Add(part);
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Texto principal de la lista; sin texto la busqueda era solo por filtros.</summary>
    public string Title => Text.Length > 0 ? Text : "(sin texto)";
}
