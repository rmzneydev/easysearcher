using System.Diagnostics;
using EasySearcher.Core;
using EasySearcher.Core.Service;

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
bool memory = false;
bool viaService = false;
bool verifyMft = false;
long? minSize = null, maxSize = null;
DateTime? modifiedFrom = null, modifiedTo = null;

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
        case "--mem":
            memory = true;
            break;
        case "--service":
            viaService = true;
            break;
        case "--from":
            modifiedFrom = DateTime.Parse(args[++i]).Date;
            break;
        case "--min":
            minSize = ParseSize(args[++i]);
            break;
        case "--max":
            maxSize = ParseSize(args[++i]);
            break;
        case "--verify-mft":
            verifyMft = true;
            break;
        case "--to":
            // Inclusivo: hasta el final de ese dia
            modifiedTo = DateTime.Parse(args[++i]).Date.AddDays(1);
            break;
        case "-k" or "--kind":
            kind = args[++i].ToLowerInvariant() switch
            {
                "files" or "f" => EntryKind.Files,
                "dirs" or "d" => EntryKind.Directories,
                "images" => EntryKind.Images,
                "videos" => EntryKind.Videos,
                "audio" => EntryKind.Audio,
                "docs" => EntryKind.Documents,
                _ => EntryKind.All,
            };
            break;
        case "-n" or "--max":
            max = int.Parse(args[++i]);
            break;
        case "-h" or "--help":
            Console.WriteLine("Uso: easysearch [-d C,D] [-r carpeta] [-k all|files|dirs|images|videos|audio|docs] [-s (incluir sistema)] [-n 50 (0 = todos)]");
            Console.WriteLine("                  [-o name|folder|size|date[-]] [-l (tamano y fecha)] [-w segundos (vigilar cambios)]");
            Console.WriteLine("                  [--from aaaa-mm-dd] [--to aaaa-mm-dd] [--min 1G] [--max 500M] [--service] [texto]");
            Console.WriteLine("                  [--verify-mft (compara tamano/fecha de la $MFT con los de Windows)]");
            Console.WriteLine();
            Console.WriteLine("Texto: linkin mp3 (ambas palabras), \"linkin park\" (exacto), *.mp3 o foto_??.jpg (comodines), !temp (excluir)");
            return 0;
        default:
            text = args[i];
            break;
    }
}

if (viaService)
    return QueryService();

if (drives.Count == 0) drives.AddRange(FileIndex.GetNtfsDrives());

var sw = Stopwatch.StartNew();
var index = FileIndex.Build(drives);
Console.Error.WriteLine($"Indice: {index.Count:N0} entradas en {sw.ElapsedMilliseconds:N0} ms");
foreach (var v in index.Volumes)
    Console.Error.WriteLine($"  {v.Drive}: {v.Count,10:N0} entradas  {v.BuildTime.TotalMilliseconds,7:N0} ms");
foreach (var (drive, error) in index.Failures)
    Console.Error.WriteLine($"  {drive}: ERROR {error.Message}");

if (memory)
{
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    using var process = Process.GetCurrentProcess();
    Console.Error.WriteLine($"Memoria: gestionada {GC.GetTotalMemory(false) / 1048576.0:N1} MB, " +
                            $"privada del proceso {process.PrivateMemorySize64 / 1048576.0:N1} MB");

    var all = index.Search("", new SearchOptions { IncludeSystem = true });
    var names = all.Select(r => r.Name).ToList();
    long chars = names.Sum(n => (long)n.Length);
    int unique = names.Distinct().Count();
    Console.Error.WriteLine($"Nombres: {names.Count:N0} ({unique:N0} distintos), {chars:N0} caracteres, " +
                            $"promedio {chars / (double)names.Count:N1}");
}

if (verifyMft)
    return VerifyMft();

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

var options = new SearchOptions
{
    Kind = kind,
    Scope = scope,
    IncludeSystem = includeSystem,
    ModifiedFrom = modifiedFrom,
    ModifiedTo = modifiedTo,
    MinSize = minSize,
    MaxSize = maxSize,
};
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
        if ((index.Update() & IndexChange.Structure) == 0) continue;
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

// Compara, para una muestra de archivos, el tamano y la fecha que da Windows (GetFileAttributesEx) con los
// leidos de la $MFT cruda
int VerifyMft()
{
    const int SampleSize = 5000;
    var files = index.Search("", new SearchOptions { Kind = EntryKind.Files });
    var sample = files.OrderBy(_ => Random.Shared.Next()).Take(SampleSize).ToList();

    // Primero lo que dice Windows (queda en cache), despues se pisa con lo leido de la $MFT
    var timer = Stopwatch.StartNew();
    var fromWindows = sample.Select(r => r.Details).ToList();
    Console.Error.WriteLine($"Windows: {sample.Count:N0} archivos consultados en {timer.ElapsedMilliseconds:N0} ms");

    timer.Restart();
    int filled = index.LoadDetailsFromMft();
    Console.Error.WriteLine($"$MFT: {filled:N0} entradas completadas en {timer.ElapsedMilliseconds:N0} ms");

    var fromMft = sample.Select(r => r.Details).ToList();
    int sizeMismatches = 0, dateMismatches = 0, shown = 0;
    for (int i = 0; i < sample.Count; i++)
    {
        var (w, m) = (fromWindows[i], fromMft[i]);
        bool sizeOk = w.Size == m.Size, dateOk = w.Modified == m.Modified;
        if (!sizeOk) sizeMismatches++;
        if (!dateOk) dateMismatches++;
        if ((!sizeOk || !dateOk) && shown++ < 10)
            Console.WriteLine($"  distinto: {sample[i].FullPath}\n    Windows {w.Size} {w.Modified:yyyy-MM-dd HH:mm:ss.fff} | $MFT {m.Size} {m.Modified:yyyy-MM-dd HH:mm:ss.fff}");
    }
    Console.Error.WriteLine($"Muestra de {sample.Count:N0}: tamano distinto en {sizeMismatches}, fecha distinta en {dateMismatches}");

    timer.Restart();
    var all = index.Search("");
    ResultSorter.Sort(all, SortColumn.Size, descending: true);
    Console.Error.WriteLine($"Ordenar {all.Count:N0} resultados por tamano (con la $MFT ya leida): {timer.ElapsedMilliseconds:N0} ms");
    return 0;
}

static long ParseSize(string value)
{
    value = value.Trim().ToUpperInvariant();
    long multiplier = value[^1] switch { 'K' => 1L << 10, 'M' => 1L << 20, 'G' => 1L << 30, 'T' => 1L << 40, _ => 1 };
    return (long)(double.Parse(multiplier == 1 ? value : value[..^1], System.Globalization.CultureInfo.InvariantCulture) * multiplier);
}

// Consulta al servicio de Windows por el named pipe; no requiere administrador
int QueryService()
{
    try
    {
        var total = Stopwatch.StartNew();
        using var client = PipeSearchClient.Connect();
        var status = client.GetStatus();
        while (!status.Ready)
        {
            Console.Error.WriteLine($"El servicio esta indexando ({status.BuildSeconds:N0} s)...");
            Thread.Sleep(1000);
            status = client.GetStatus();
        }
        Console.Error.WriteLine($"Servicio: {status.Entries:N0} entradas en {string.Join(" ", status.Drives)}");

        var sw = Stopwatch.StartNew();
        if ((modifiedFrom is not null || modifiedTo is not null || kind > EntryKind.Directories) &&
            status.ProtocolVersion < Protocol.Version)
        {
            Console.Error.WriteLine("ERROR: el servicio instalado es una version anterior y no filtra por fecha ni por tipo.");
            return 1;
        }
        var response = client.Search(new SearchRequest(text, kind, root, includeSystem, sort, descending,
            ModifiedFrom: modifiedFrom, ModifiedTo: modifiedTo, MinSize: minSize, MaxSize: maxSize));
        var rows = client.GetRows(response.SearchId, 0, max > 0 ? max : response.Total);
        var detailsRows = details ? client.GetDetails(response.SearchId, 0, rows.Length) : [];
        double roundTrip = sw.Elapsed.TotalMilliseconds;

        for (int i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            var line = $"{(r.IsDirectory ? "[D]" : "   ")} {Path.Join(r.Directory, r.Name)}";
            if (details && i < detailsRows.Length)
                line = $"{detailsRows[i].Size?.ToString() ?? "",12}  {detailsRows[i].Modified:yyyy-MM-dd HH:mm}  {line}";
            Console.WriteLine(line);
        }
        Console.Error.WriteLine($"\n{response.Total:N0} coincidencias{(response.Root is null ? "" : " en " + response.Root)}: " +
                                $"busqueda {response.ElapsedMs:N1} ms en el servicio, {roundTrip:N1} ms ida y vuelta, " +
                                $"{total.Elapsed.TotalMilliseconds:N0} ms en total");
        return 0;
    }
    catch (SearchServiceException ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
}

string Format(SearchResult r)
{
    var line = r.IsDirectory ? $"[D] {r.FullPath}" : $"    {r.FullPath}";
    if (!details) return line;
    var d = r.Details;
    return $"{d.Size?.ToString() ?? "",12}  {d.Modified:yyyy-MM-dd HH:mm}  {line}";
}
