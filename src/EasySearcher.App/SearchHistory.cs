using System.IO;
using System.Text.Json;

namespace EasySearcher.App;

/// <summary>Historial de busquedas en %APPDATA%\EasySearcher\history.json, la mas reciente primero.</summary>
sealed class SearchHistory
{
    const int MaxEntries = 50;
    // Si en este tiempo se usa una busqueda que extiende otra ("linkin" -> "linkin park"), la corta sobra
    static readonly TimeSpan RefinementWindow = TimeSpan.FromMinutes(5);

    static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasySearcher", "history.json");

    readonly string _path;
    readonly List<SearchState> _entries;

    SearchHistory(string path, List<SearchState> entries)
    {
        _path = path;
        _entries = entries;
    }

    public IReadOnlyList<SearchState> Entries => _entries;

    public static SearchHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return new SearchHistory(path, JsonSerializer.Deserialize<List<SearchState>>(File.ReadAllText(path)) ?? []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new SearchHistory(path, []);
    }

    public void Add(SearchState entry)
    {
        _entries.RemoveAll(e => e.SameSearch(entry) ||
                                (e.SameContext(entry) && entry.LastUsed - e.LastUsed < RefinementWindow &&
                                 entry.Text.StartsWith(e.Text, StringComparison.OrdinalIgnoreCase)));
        _entries.Insert(0, entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        Save();
    }

    public void Remove(SearchState entry)
    {
        if (_entries.Remove(entry)) Save();
    }

    public void Clear()
    {
        _entries.Clear();
        Save();
    }

    /// <summary>Las que contienen <paramref name="text"/> (todas si esta vacio), la mas reciente primero.</summary>
    public List<SearchState> Matching(string text) => text.Length == 0
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
