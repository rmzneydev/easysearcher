namespace EasySearcher.Core;

/// <summary>Extensiones (sin punto) de cada categoria de <see cref="EntryKind"/>.</summary>
public static class FileCategories
{
    static readonly Dictionary<EntryKind, HashSet<string>> Extensions = new()
    {
        [EntryKind.Images] = Set(
            "jpg", "jpeg", "jfif", "png", "gif", "bmp", "webp", "heic", "heif", "avif", "tif", "tiff", "ico", "svg",
            "psd", "raw", "dng", "cr2", "cr3", "nef", "arw", "orf", "rw2", "raf"),
        [EntryKind.Videos] = Set(
            "mp4", "m4v", "mkv", "avi", "mov", "wmv", "flv", "webm", "mpg", "mpeg", "3gp", "ts", "m2ts", "mts", "vob"),
        [EntryKind.Audio] = Set(
            "mp3", "flac", "wav", "aac", "m4a", "ogg", "opus", "wma", "aiff", "aif", "alac", "mid", "midi"),
        [EntryKind.Documents] = Set(
            "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "rtf", "txt", "md", "csv", "epub"),
    };

    static HashSet<string> Set(params string[] items) => new(items, StringComparer.OrdinalIgnoreCase);

    /// <summary>Busqueda por span de la extension (sin crear strings en el bucle de busqueda); null si la categoria no filtra por extension.</summary>
    internal static HashSet<string>.AlternateLookup<ReadOnlySpan<char>>? GetLookup(EntryKind kind) =>
        Extensions.TryGetValue(kind, out var set) ? set.GetAlternateLookup<ReadOnlySpan<char>>() : null;

    /// <summary>Extension sin punto ("foto.JPG" -> "JPG"); vacia si no tiene.</summary>
    internal static ReadOnlySpan<char> GetExtension(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0 ? [] : name[(dot + 1)..];
    }
}
