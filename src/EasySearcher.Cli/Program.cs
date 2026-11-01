using System.Diagnostics;
using EasySearcher.Core;

// Uso: easysearch [-d C,D] [-k all|files|dirs] [-n 50] [texto]
// Requiere ejecutarse como administrador.

var drives = new List<char>();
var kind = EntryKind.All;
int max = 50;
string text = "";

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-d" or "--drives":
            drives.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s[0]));
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
            Console.WriteLine("Uso: easysearch [-d C,D] [-k all|files|dirs] [-n 50 (0 = todos)] [texto]");
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

sw.Restart();
var results = index.Search(text, kind);
var searchMs = sw.Elapsed.TotalMilliseconds;

foreach (var r in max > 0 ? results.Take(max) : results)
    Console.WriteLine(r.IsDirectory ? $"[D] {r.FullPath}" : $"    {r.FullPath}");

Console.Error.WriteLine($"\n{results.Count:N0} coincidencias en {searchMs:N1} ms" +
    (max > 0 && results.Count > max ? $" (mostrando {max}; usa -n 0 para ver todas)" : ""));

return index.Volumes.Count > 0 ? 0 : 1;
