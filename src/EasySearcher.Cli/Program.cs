using System.Diagnostics;
using EasySearcher.Core;

// Uso: easysearch [-d C,D] [-r carpeta] [-k all|files|dirs] [-s] [-n 50] [-w segundos] [texto]
// Requiere ejecutarse como administrador.

var drives = new List<char>();
var kind = EntryKind.All;
int max = 50;
string text = "";
string? root = null;
bool includeSystem = false;
int watchSeconds = 0;
var sort = SortColumn.None;
bool descending = false;
bool details = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-d" or "--drives":
            drives.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s[0]));
            break;
        case "-r" or "--root":
            root = args[++i];
            break;
        case "-s" or "--system":
            includeSystem = true;
            break;
        case "-w" or "--watch":
            watchSeconds = int.Parse(args[++i]);
            break;
        case "-o" or "--sort":
            // name, folder, size, date; con "-" al final en orden descendente (p. ej. size-)
            var spec = args[++i].ToLowerInvariant();
            descending = spec.EndsWith('-');
            sort = spec.TrimEnd('-') switch
            {
                "name" => SortColumn.Name,
                "folder" => SortColumn.Folder,
                "size" => SortColumn.Size,
                "date" => SortColumn.Modified,
                _ => SortColumn.None,
            };
            break;
        case "-l" or "--long":
            details = true;
            break;
        case "-k" or "--kind":
            kind = args[++i].ToLowerInvariant() switch
            {
                "files" or "f" => EntryKind.Files,
                "dirs" or "d" => EntryKind.Directories,
                _ => EntryKind.All,
            };
            break;
        case "-n" or "--max":
            max = int.Parse(args[++i]);
            break;
        case "-h" or "--help":
            Console.WriteLine("Uso: easysearch [-d C,D] [-r carpeta] [-k all|files|dirs] [-s (incluir sistema)] [-n 50 (0 = todos)]");
            Console.WriteLine("                  [-o name|folder|size|date[-]] [-l (tamano y fecha)] [-w segundos (vigilar cambios)] [texto]");
            return 0;
        default:
            text = args[i];
            break;
    }
}

if (drives.Count == 0) drives.AddRange(FileIndex.GetNtfsDrives());

var sw = Stopwatch.StartNew();
var index = FileIndex.Build(drives);
Console.Error.WriteLine($"Indice: {index.Count:N0} entradas en {sw.ElapsedMilliseconds:N0} ms");
foreach (var v in index.Volumes)
    Console.Error.WriteLine($"  {v.Drive}: {v.Count,10:N0} entradas  {v.BuildTime.TotalMilliseconds,7:N0} ms");
foreach (var (drive, error) in index.Failures)
    Console.Error.WriteLine($"  {drive}: ERROR {error.Message}");

SearchScope? scope = null;
if (root is not null)
{
    sw.Restart();
    try
    {
        scope = index.CreateScope(root);
    }
    catch (DirectoryNotFoundException ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
    Console.Error.WriteLine($"Raiz: {scope.RootPath} ({sw.Elapsed.TotalMilliseconds:N0} ms)");
}

var options = new SearchOptions { Kind = kind, Scope = scope, IncludeSystem = includeSystem };
sw.Restart();
var results = index.Search(text, options);
var searchMs = sw.Elapsed.TotalMilliseconds;
if (sort != SortColumn.None)
{
    sw.Restart();
    ResultSorter.Sort(results, sort, descending);
    Console.Error.WriteLine($"Ordenado por {sort}{(descending ? " (desc)" : "")} en {sw.Elapsed.TotalMilliseconds:N0} ms");
}

foreach (var r in max > 0 ? results.Take(max) : results)
    Console.WriteLine(Format(r));

Console.Error.WriteLine($"\n{results.Count:N0} coincidencias en {searchMs:N1} ms" +
    (max > 0 && results.Count > max ? $" (mostrando {max}; usa -n 0 para ver todas)" : ""));

if (watchSeconds > 0)
{
    // Muestra lo que entra (+) y sale (-) de los resultados a medida que cambia el disco
    Console.Error.WriteLine($"Vigilando cambios durante {watchSeconds} s...");
    var current = results.Select(Format).ToHashSet();
    var until = DateTime.UtcNow.AddSeconds(watchSeconds);
    while (DateTime.UtcNow < until)
    {
        Thread.Sleep(500);
        sw.Restart();
        if (!index.Update()) continue;
        var updateMs = sw.Elapsed.TotalMilliseconds;

        var next = index.Search(text, options).Select(Format).ToHashSet();
        foreach (var line in current.Except(next)) Console.WriteLine($"- {line}");
        foreach (var line in next.Except(current)) Console.WriteLine($"+ {line}");
        Console.Error.WriteLine($"  (cambios aplicados en {updateMs:N1} ms; {next.Count:N0} coincidencias)");
        current = next;
    }
}

index.Dispose();
return index.Volumes.Count > 0 ? 0 : 1;

string Format(SearchResult r)
{
    var line = r.IsDirectory ? $"[D] {r.FullPath}" : $"    {r.FullPath}";
    if (!details) return line;
    var d = r.Details;
    return $"{d.Size?.ToString() ?? "",12}  {d.Modified:yyyy-MM-dd HH:mm}  {line}";
}
