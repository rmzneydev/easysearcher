using System.IO;
using System.Text.Json;
using EasySearcher.Core;

namespace EasySearcher.App;

/// <summary>
/// Una busqueda que llego a usarse (se abrio, copio o arrastro un resultado) con todo su contexto, para
/// repetirla despues. Las fechas relativas ("esta semana") se guardan como opcion, no como fechas fijas.
/// </summary>
sealed record HistoryEntry(
    string Text,
    string? Root,
    EntryKind Kind,
    DatePreset Date,
    DateTime? CustomFrom,
    DateTime? CustomTo,
    SizeCriterion Size)
{
    public DateTime LastUsed { get; init; }

    public bool SameContext(HistoryEntry other) =>
        string.Equals(Root, other.Root, StringComparison.OrdinalIgnoreCase) &&
        Kind == other.Kind && Date == other.Date && Size == other.Size &&
        CustomFrom == other.CustomFrom && CustomTo == other.CustomTo;

    public bool SameSearch(HistoryEntry other) =>
        SameContext(other) && string.Equals(Text.Trim(), other.Text.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Contexto para la lista, p. ej. "en D:\Musica · imágenes · este mes".</summary>
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

/// <summary>Historial de busquedas en %APPDATA%\EasySearcher\history.json, la mas reciente primero.</summary>
sealed class SearchHistory
{
    const int MaxEntries = 50;
    // Si en este tiempo se usa una busqueda que extiende otra ("linkin" -> "linkin park"), la corta sobra
    static readonly TimeSpan RefinementWindow = TimeSpan.FromMinutes(5);

    static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasySearcher", "history.json");

    readonly string _path;
    readonly List<HistoryEntry> _entries;

    SearchHistory(string path, List<HistoryEntry> entries)
    {
        _path = path;
        _entries = entries;
    }

    public IReadOnlyList<HistoryEntry> Entries => _entries;

    public static SearchHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return new SearchHistory(path, JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(path)) ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new SearchHistory(path, []);
    }

    public void Add(HistoryEntry entry)
    {
        _entries.RemoveAll(e => e.SameSearch(entry) ||
                                (e.SameContext(entry) && entry.LastUsed - e.LastUsed < RefinementWindow &&
                                 entry.Text.StartsWith(e.Text, StringComparison.OrdinalIgnoreCase)));
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    public void Remove(HistoryEntry entry)
    {
        if (_entries.Remove(entry)) Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    /// <summary>Las que contienen <paramref name="text"/> (todas si esta vacio), la mas reciente primero.</summary>
    public List<HistoryEntry> Matching(string text) => text.Length == 0
        ? [.. _entries]
        : _entries.Where(e => e.Text.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
