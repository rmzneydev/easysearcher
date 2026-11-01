namespace EasySearcher.Core;

public enum SortColumn
{
    None,
    Name,
    Folder,
    Size,
    Modified,
}

public static class ResultSorter
{
    /// <summary>
    /// Ordena los resultados en su lugar. Los empates se resuelven por nombre y luego por posicion,
    /// asi el orden es siempre el mismo. Por tamano o fecha consulta el disco para lo que no este en cache.
    /// </summary>
    public static void Sort(List<SearchResult> results, SortColumn column, bool descending, CancellationToken cancellationToken = default)
    {
        int n = results.Count;
        if (column == SortColumn.None || n < 2) return;

        var parallel = new ParallelOptions { CancellationToken = cancellationToken };
        var names = new string[n];
        Parallel.For(0, n, parallel, i => names[i] = results[i].Name);

        Comparison<int> primary;
        switch (column)
        {
            case SortColumn.Folder:
                var folders = new string[n];
                Parallel.For(0, n, parallel, i => folders[i] = results[i].DirectoryPath);
                primary = (a, b) => StringComparer.OrdinalIgnoreCase.Compare(folders[a], folders[b]);
                break;
            case SortColumn.Size:
                // Carpetas y desconocidos (-1) quedan antes que los archivos al ordenar de menor a mayor
                var sizes = new long[n];
                Parallel.For(0, n, parallel, i => sizes[i] = results[i].Size ?? -1);
                primary = (a, b) => sizes[a].CompareTo(sizes[b]);
                break;
            case SortColumn.Modified:
                var dates = new long[n];
                Parallel.For(0, n, parallel, i => dates[i] = results[i].Modified?.Ticks ?? 0);
                primary = (a, b) => dates[a].CompareTo(dates[b]);
                break;
            default:
                primary = (a, b) => 0;
                break;
        }
        cancellationToken.ThrowIfCancellationRequested();

        int sign = descending ? -1 : 1;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int c = primary(a, b);
            if (c == 0) c = StringComparer.OrdinalIgnoreCase.Compare(names[a], names[b]);
            if (c == 0) c = a.CompareTo(b);
            return sign * c;
        });

        var sorted = new SearchResult[n];
        for (int i = 0; i < n; i++) sorted[i] = results[order[i]];
        results.Clear();
        results.AddRange(sorted);
    }
}
