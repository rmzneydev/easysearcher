namespace EasySearcher.Core;

/// <summary>
/// Cache de tamano y fecha por entrada, en paginas que se crean solo al usarse: si nunca se ordena por
/// tamano, la mayor parte del indice no ocupa memoria aqui.
/// Agrandar (EnsureCapacity) requiere el lock de escritura del indice; leer y escribir valores, solo el de lectura.
/// </summary>
sealed class DetailsCache
{
    const int PageBits = 12;
    const int PageSize = 1 << PageBits;
    const int PageMask = PageSize - 1;

    public const long Unknown = long.MinValue;

    // Cada pagina intercala [tamano, fecha] por entrada
    long[]?[] _pages = [];

    public void EnsureCapacity(int count)
    {
        int needed = (count + PageSize - 1) >> PageBits;
        if (needed > _pages.Length)
            Array.Resize(ref _pages, Math.Max(needed, _pages.Length * 2));
    }

    public bool TryGet(int entry, out long size, out long modified)
    {
        var page = _pages[entry >> PageBits];
        int k = (entry & PageMask) * 2;
        if (page is null || page[k + 1] == Unknown)
        {
            size = modified = Unknown;
            return false;
        }
        size = page[k];
        modified = page[k + 1];
        return true;
    }

    public void Set(int entry, long size, long modified)
    {
        ref var slot = ref _pages[entry >> PageBits];
        var page = Volatile.Read(ref slot) ?? CreatePage(ref slot);
        int k = (entry & PageMask) * 2;
        page[k] = size;
        page[k + 1] = modified;
    }

    public void Invalidate(int entry)
    {
        var page = _pages[entry >> PageBits];
        if (page is not null) page[(entry & PageMask) * 2 + 1] = Unknown;
    }

    static long[] CreatePage(ref long[]? slot)
    {
        var page = new long[PageSize * 2];
        Array.Fill(page, Unknown);
        // Dos hilos pueden crear la misma pagina a la vez; se queda la primera
        return Interlocked.CompareExchange(ref slot, page, null) ?? page;
    }
}
