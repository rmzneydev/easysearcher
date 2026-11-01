using System.Collections;
using EasySearcher.Core.Service;

namespace EasySearcher.App;

/// <summary>
/// Lista de solo lectura que la ListView recorre como si tuviera todos los resultados, pero que pide
/// las filas al motor por paginas a medida que se muestran. El tamano y la fecha de cada pagina se
/// piden aparte, en segundo plano.
/// </summary>
sealed class VirtualResultList : IList
{
    const int PageSize = 100;
    const int MaxCachedPages = 300;

    readonly ISearchService _service;
    readonly Action _onExpired;
    readonly Dictionary<int, ResultRow[]> _pages = [];
    bool _expired;

    public int SearchId { get; }
    public int Count { get; }

    /// <param name="onExpired">Se llama (una vez) si el motor ya no tiene guardada la busqueda.</param>
    public VirtualResultList(ISearchService service, int searchId, int count, Action onExpired)
    {
        _service = service;
        SearchId = searchId;
        Count = count;
        _onExpired = onExpired;
    }

    public ResultRow this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return GetPage(index / PageSize)[index % PageSize];
        }
    }

    ResultRow[] GetPage(int page)
    {
        if (_pages.TryGetValue(page, out var rows)) return rows;

        int start = page * PageSize;
        int length = Math.Min(PageSize, Count - start);
        RowData[] data;
        try
        {
            data = _service.GetRows(SearchId, start, length);
        }
        catch (SearchServiceException)
        {
            data = [];
        }

        if (data.Length < length && !_expired)
        {
            _expired = true;
            _onExpired();
        }

        rows = new ResultRow[length];
        for (int i = 0; i < length; i++)
            rows[i] = new ResultRow(i < data.Length ? data[i] : new RowData('?', -1 - start - i, "", "", false));

        if (_pages.Count >= MaxCachedPages) _pages.Clear();
        _pages[page] = rows;
        if (data.Length > 0) LoadDetails(start, rows);
        return rows;
    }

    async void LoadDetails(int start, ResultRow[] rows)
    {
        RowDetails[] details;
        try
        {
            details = await Task.Run(() => _service.GetDetails(SearchId, start, rows.Length));
        }
        catch (SearchServiceException)
        {
            return;
        }

        // De vuelta en el hilo de la interfaz
        for (int i = 0; i < details.Length && i < rows.Length; i++)
        {
            rows[i].Size = details[i].Size;
            rows[i].Modified = details[i].Modified;
        }
    }

    /// <summary>Busca solo en las paginas ya cargadas: pedir todas al motor seria demasiado caro.</summary>
    public int IndexOf(object? value)
    {
        if (value is not ResultRow row) return -1;
        foreach (var (page, rows) in _pages)
        {
            int i = Array.IndexOf(rows, row);
            if (i >= 0) return page * PageSize + i;
        }
        return -1;
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return this[i];
    }

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++)
            array.SetValue(this[i], index + i);
    }

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
