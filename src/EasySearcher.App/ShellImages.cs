using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EasySearcher.App;

enum ShellImageKind
{
    /// <summary>Icono de 16 px para la vista de lista.</summary>
    SmallIcon,
    /// <summary>Miniatura (o icono si no tiene) para "Iconos medianos".</summary>
    MediumThumbnail,
    /// <summary>Miniatura (o icono si no tiene) para "Iconos grandes".</summary>
    LargeThumbnail,
}

/// <summary>
/// Iconos y miniaturas del shell de Windows (los mismos del Explorador, con su cache de miniaturas).
/// Se generan en hilos STA en segundo plano; se atiende primero lo ultimo que se pidio y, si la cola crece
/// (scroll rapido), se descarta lo mas viejo, que ya no esta en pantalla. Todo el estado salvo la cola vive
/// en el hilo de la interfaz.
/// </summary>
static class ShellImages
{
    public const int MediumSize = 96;
    public const int LargeSize = 192;

    const int WorkerCount = 3;
    const int MaxQueued = 300;
    // Un generador de miniaturas de terceros puede colgarse: pasado este tiempo se abandona su hilo
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    // Tope de hilos de reemplazo, para que un generador que siempre se cuelga no los multiplique sin fin
    const int MaxReplacements = 6;

    // Estos tipos tienen un icono propio por archivo; el resto comparte el icono de su extension
    static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "lnk", "ico", "url", "appref-ms", "msc", "cpl", "scr",
    };

    static readonly Dispatcher UiDispatcher = Application.Current.Dispatcher;
    static readonly LruCache<string, BitmapSource> SmallCache = new(2000);
    static readonly LruCache<string, BitmapSource> MediumCache = new(500);
    static readonly LruCache<string, BitmapSource> LargeCache = new(200);
    static readonly HashSet<string> Failed = [];
    static readonly Dictionary<string, Request> Pending = [];
    static readonly LinkedList<Request> Queue = new();
    static readonly List<Worker> Workers = [];
    static readonly Timer Watchdog;
    static int _replacements;

    /// <summary>Escala de pixeles por DIP de la pantalla, para pedir imagenes nitidas.</summary>
    public static double Scale { get; set; } = 1;

    static ShellImages()
    {
        for (int i = 0; i < WorkerCount; i++)
            StartWorker();
        Watchdog = new Timer(_ => CheckHungWorkers(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    static void StartWorker()
    {
        var worker = new Worker();
        lock (Workers) Workers.Add(worker);
        var thread = new Thread(() => Work(worker)) { IsBackground = true, Name = "ShellImages" };
        // El shell exige COM en modo STA
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>
    /// Abandona los hilos que llevan demasiado con una imagen: la marca como fallida y crea otro hilo.
    /// El hilo colgado no se puede detener; si algun dia termina, descarta su resultado y sale.
    /// </summary>
    static void CheckHungWorkers()
    {
        var now = Environment.TickCount64;
        List<Request> abandoned = [];
        lock (Workers)
        {
            foreach (var worker in Workers.ToList())
            {
                if (worker.Current is not { } request || now - worker.StartedAt < Timeout.TotalMilliseconds) continue;
                worker.Abandoned = true;
                Workers.Remove(worker);
                abandoned.Add(request);
                if (_replacements++ < MaxReplacements) StartWorker();
            }
        }
        foreach (var request in abandoned)
            UiDispatcher.InvokeAsync(() => Complete(request, null), DispatcherPriority.Background);
    }

    /// <summary>
    /// Devuelve la imagen si ya esta en cache; si no, la pide en segundo plano y devuelve null.
    /// Cuando llega, avisa a la fila (PropertyChanged) para que la vista la vuelva a leer.
    /// </summary>
    public static ImageSource? Get(ResultRow row, ShellImageKind kind, string propertyName)
    {
        if (row.Name.Length == 0) return null;

        var (cache, key, size, iconOnly) = Describe(row, kind);
        if (cache.TryGet(key, out var image)) return image;
        if (Failed.Contains(key)) return null;

        if (Pending.TryGetValue(key, out var pending))
        {
            pending.Waiting.Add((row, propertyName));
            return null;
        }

        var request = new Request(key, row.FullPath, size, iconOnly, cache);
        request.Waiting.Add((row, propertyName));
        Pending[key] = request;
        lock (Queue)
        {
            Queue.AddFirst(request);
            if (Queue.Count > MaxQueued)
            {
                Pending.Remove(Queue.Last!.Value.Key);
                Queue.RemoveLast();
            }
            Monitor.Pulse(Queue);
        }
        return null;
    }

    static (LruCache<string, BitmapSource> Cache, string Key, int Size, bool IconOnly) Describe(ResultRow row, ShellImageKind kind)
    {
        switch (kind)
        {
            case ShellImageKind.SmallIcon:
                int small = (int)Math.Round(16 * Scale);
                var extension = Path.GetExtension(row.Name).TrimStart('.');
                string key = row.IsDirectory ? "<carpeta>"
                    : PerFileIconExtensions.Contains(extension) ? row.FullPath
                    : "." + extension.ToLowerInvariant();
                return (SmallCache, key, small, true);
            case ShellImageKind.MediumThumbnail:
                int medium = (int)Math.Round(MediumSize * Scale);
                return (MediumCache, row.FullPath, medium, false);
            default:
                int large = (int)Math.Round(LargeSize * Scale);
                return (LargeCache, row.FullPath, large, false);
        }
    }

    static void Work(Worker worker)
    {
        while (true)
        {
            Request request;
            lock (Queue)
            {
                while (Queue.Count == 0) Monitor.Wait(Queue);
                request = Queue.First!.Value;
                Queue.RemoveFirst();
            }

            lock (Workers)
            {
                worker.StartedAt = Environment.TickCount64;
                worker.Current = request;
            }
            var image = Load(request.Path, request.Size, request.IconOnly);
            lock (Workers)
            {
                // El vigilante ya dio esta imagen por fallida y puso otro hilo en su lugar
                if (worker.Abandoned) return;
                worker.Current = null;
            }
            UiDispatcher.InvokeAsync(() => Complete(request, image), DispatcherPriority.Background);
        }
    }

    static void Complete(Request request, BitmapSource? image)
    {
        // Solo si sigue siendo la pendiente para esa clave (no una pedida de nuevo mas tarde)
        if (Pending.TryGetValue(request.Key, out var pending) && pending != request) return;
        Pending.Remove(request.Key);
        if (image is null)
        {
            Failed.Add(request.Key);
            return;
        }
        request.Cache.Add(request.Key, image);
        foreach (var (row, property) in request.Waiting)
            row.NotifyChanged(property);
    }

    static BitmapSource? Load(string path, int size, bool iconOnly)
    {
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            try
            {
                var flags = iconOnly ? SIIGBF.IconOnly : SIIGBF.ResizeToFit;
                if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out bitmap) != 0) return null;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
            return ToBitmapSource(bitmap);
        }
        catch (COMException)
        {
            // Archivo que ya no existe, sin permisos o sin generador de miniaturas
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }

    /// <summary>
    /// Copia el HBITMAP (32 bits, alfa premultiplicado) a un BitmapSource. No se usa
    /// Imaging.CreateBitmapSourceFromHBitmap porque pierde la transparencia de los iconos.
    /// </summary>
    static BitmapSource? ToBitmapSource(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0) return null;
        int width = info.bmWidth, height = info.bmHeight, stride = width * 4;

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // negativo = filas de arriba hacia abajo
            biPlanes = 1,
            biBitCount = 32,
        };
        var pixels = new byte[stride * height];
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        // Algunas miniaturas (fotos) vienen con el canal alfa en cero: son opacas
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0) { hasAlpha = true; break; }
        }
        var format = hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32;

        var source = BitmapSource.Create(width, height, 96, 96, format, null, pixels, stride);
        source.Freeze();
        return source;
    }

    // class y no record: se compara por referencia (dos pedidos de la misma imagen son distintos)
    sealed class Request(string key, string path, int size, bool iconOnly, LruCache<string, BitmapSource> cache)
    {
        public string Key { get; } = key;
        public string Path { get; } = path;
        public int Size { get; } = size;
        public bool IconOnly { get; } = iconOnly;
        public LruCache<string, BitmapSource> Cache { get; } = cache;
        public List<(ResultRow Row, string Property)> Waiting { get; } = [];
    }

    /// <summary>Estado de un hilo de carga, para el vigilante. Se lee y escribe bajo lock(Workers).</summary>
    sealed class Worker
    {
        public Request? Current;
        public long StartedAt;
        public bool Abandoned;
    }

    [Flags]
    enum SIIGBF
    {
        ResizeToFit = 0x00,
        IconOnly = 0x04,
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr window, IntPtr dc);
}

/// <summary>Cache de tamano fijo que descarta lo usado hace mas tiempo. No es thread-safe.</summary>
sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = [];
    readonly LinkedList<(TKey Key, TValue Value)> _order = new();

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        if (_map.Remove(key, out var existing)) _order.Remove(existing);
        _map[key] = _order.AddFirst((key, value));
        if (_map.Count > capacity)
        {
            _map.Remove(_order.Last!.Value.Key);
            _order.RemoveLast();
        }
    }
}
