using System.Diagnostics;
using System.Text;
using EasySearcher.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace EasySearcher.Core;

/// <summary>
/// Indice en memoria de todas las entradas (archivos y carpetas) de un volumen NTFS, que se mantiene
/// al dia leyendo el USN Journal con <see cref="Update"/>.
/// Se guarda como listas paralelas para ocupar poca memoria. Las entradas borradas solo se marcan,
/// asi los indices que ya tiene la interfaz siguen siendo validos.
/// </summary>
public sealed class VolumeIndex : IDisposable
{
    const ulong RootFrn = 5;
    const int MaxDepth = 512;

    // Marcas por entrada
    const byte Orphan = 0x1;      // su cadena de padres no llega a la raiz: metarchivos de NTFS ($Extend, ...)
    const byte SystemTree = 0x2;  // ella o alguna carpeta que la contiene es oculta + sistema
    const byte Deleted = 0x4;
    const byte Inherited = Orphan | SystemTree;
    const byte Computed = 0x80;
    const FileAttributes ProtectedAttributes = FileAttributes.Hidden | FileAttributes.System;

    // Valores especiales de la cache de tamano/fecha
    const long Unknown = long.MinValue;          // aun no se ha leido
    const long Unavailable = long.MinValue + 1;  // no se pudo leer (sin permisos, ya no existe...)
    const long NoSize = -1;                      // carpetas

    readonly ReaderWriterLockSlim _lock = new();
    readonly SafeFileHandle _volume;

    List<ulong> _frn = [];
    List<ulong> _parent = [];
    List<uint> _attributes = [];
    List<string> _names = [];
    List<byte> _flags = [];
    List<long> _size = [];
    List<long> _modified = []; // ticks UTC
    Dictionary<ulong, int> _byFrn = [];
    int _deletedCount;
    bool _flagsDirty;

    JournalInfo? _journal;
    long _nextUsn;

    /// <summary>Cambia cada vez que se vuelve a enumerar la MFT (los indices de entrada dejan de valer).</summary>
    internal int Generation { get; private set; }

    /// <summary>Cambia con cada lote de cambios aplicado.</summary>
    internal int Version { get; private set; }

    public char Drive { get; }
    public TimeSpan BuildTime { get; private set; }

    /// <summary>True si el volumen tiene USN Journal y el indice se actualiza en vivo.</summary>
    public bool IsLive => _journal is not null;

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _names.Count - _deletedCount; }
            finally { _lock.ExitReadLock(); }
        }
    }

    VolumeIndex(char drive, SafeFileHandle volume)
    {
        Drive = drive;
        _volume = volume;
    }

    public static VolumeIndex Build(char drive)
    {
        drive = char.ToUpperInvariant(drive);
        var volume = MftReader.OpenVolume(drive);
        var index = new VolumeIndex(drive, volume);
        try
        {
            index.Load();
        }
        catch
        {
            index.Dispose();
            throw;
        }
        return index;
    }

    /// <summary>Enumera la MFT completa y reemplaza el contenido del indice.</summary>
    void Load()
    {
        var sw = Stopwatch.StartNew();

        // Se toma la posicion del journal antes de enumerar para no perder cambios ocurridos mientras tanto
        var journal = MftReader.QueryJournal(_volume, Drive);

        var frn = new List<ulong>(1 << 16);
        var parent = new List<ulong>(1 << 16);
        var attributes = new List<uint>(1 << 16);
        var names = new List<string>(1 << 16);

        MftReader.Enumerate(_volume, Drive, (f, p, a, _, name) =>
        {
            if (f == RootFrn) return;
            frn.Add(f);
            parent.Add(p);
            attributes.Add(a);
            names.Add(new string(name));
        });

        var byFrn = new Dictionary<ulong, int>(frn.Count);
        for (int i = 0; i < frn.Count; i++)
            byFrn[frn[i]] = i;

        _lock.EnterWriteLock();
        try
        {
            _frn = frn;
            _parent = parent;
            _attributes = attributes;
            _names = names;
            _byFrn = byFrn;
            _flags = new List<byte>(new byte[frn.Count]);
            var unknown = new long[frn.Count];
            Array.Fill(unknown, Unknown);
            _size = new List<long>(unknown);
            _modified = new List<long>(unknown);
            _deletedCount = 0;
            ComputeFlags();
            _journal = journal;
            _nextUsn = journal?.NextUsn ?? 0;
            Generation++;
            Version++;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        BuildTime = sw.Elapsed;
    }

    /// <summary>
    /// Aplica los cambios pendientes del USN Journal. Devuelve true si cambiaron nombres, ubicaciones o
    /// atributos; los cambios solo de contenido descartan el tamano/fecha en cache pero devuelven false.
    /// No es seguro llamarlo desde varios hilos a la vez.
    /// </summary>
    public bool Update()
    {
        if (_journal is not { } journal) return false;

        var records = new List<JournalRecord>();
        long nextUsn = _nextUsn;
        if (!MftReader.ReadJournal(_volume, Drive, journal.Id, ref nextUsn, records))
        {
            Load();
            return true;
        }
        _nextUsn = nextUsn;
        if (records.Count == 0) return false;

        bool structural = false;
        _lock.EnterWriteLock();
        try
        {
            foreach (var record in records)
                structural |= Apply(record);
            if (_flagsDirty) ComputeFlags();
            if (structural) Version++;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        return structural;
    }

    /// <summary>Aplica un registro del journal. Devuelve true si cambio la estructura del indice.</summary>
    bool Apply(in JournalRecord r)
    {
        if (r.Frn == RootFrn) return false;
        bool exists = _byFrn.TryGetValue(r.Frn, out int i);

        if ((r.Reason & MftReader.StructuralReasons) == 0)
        {
            // Solo cambio el contenido: basta con olvidar el tamano y la fecha
            if (exists)
            {
                _size[i] = Unknown;
                _modified[i] = Unknown;
            }
            return false;
        }

        if ((r.Reason & MftReader.ReasonFileDelete) != 0)
        {
            if (!exists || (_flags[i] & Deleted) != 0) return false;
            _flags[i] |= Deleted;
            _deletedCount++;
            return true;
        }

        // El registro con el nombre viejo de un renombrado no aporta nada: le sigue el del nombre nuevo
        if ((r.Reason & (MftReader.ReasonRenameOldName | MftReader.ReasonRenameNewName)) == MftReader.ReasonRenameOldName)
            return false;

        if (exists)
        {
            bool wasDeleted = (_flags[i] & Deleted) != 0;
            if (wasDeleted) _deletedCount--;

            // Si una carpeta se mueve o cambia de oculta/sistema, cambian las marcas de todo su contenido
            bool isDirectory = (r.Attributes & (uint)FileAttributes.Directory) != 0;
            if (isDirectory && (wasDeleted || _parent[i] != r.Parent ||
                                ((_attributes[i] ^ r.Attributes) & (uint)ProtectedAttributes) != 0))
                _flagsDirty = true;

            _parent[i] = r.Parent;
            _attributes[i] = r.Attributes;
            _names[i] = r.Name;
            _size[i] = Unknown;
            _modified[i] = Unknown;
        }
        else
        {
            i = _names.Count;
            _frn.Add(r.Frn);
            _parent.Add(r.Parent);
            _attributes.Add(r.Attributes);
            _names.Add(r.Name);
            _flags.Add(0);
            _size.Add(Unknown);
            _modified.Add(Unknown);
            _byFrn[r.Frn] = i;
        }

        _flags[i] = ComputeEntryFlags(i);
        return true;
    }

    /// <summary>Marcas de una entrada a partir de las de su padre.</summary>
    byte ComputeEntryFlags(int entry)
    {
        byte flags;
        ulong p = _parent[entry];
        if (p == RootFrn) flags = 0;
        else if (_byFrn.TryGetValue(p, out int j) && (_flags[j] & Deleted) == 0) flags = (byte)(_flags[j] & Inherited);
        else flags = Orphan;

        if ((_attributes[entry] & (uint)ProtectedAttributes) == (uint)ProtectedAttributes) flags |= SystemTree;
        return flags;
    }

    /// <summary>Recalcula Orphan y SystemTree de todas las entradas; cada cadena de padres se recorre una sola vez.</summary>
    void ComputeFlags()
    {
        var state = new byte[_names.Count];
        var pending = new Stack<int>();

        for (int i = 0; i < state.Length; i++)
        {
            if (state[i] != 0) continue;

            int current = i;
            byte inherited;
            while (true)
            {
                pending.Push(current);
                ulong p = _parent[current];
                if (p == RootFrn) { inherited = 0; break; }
                if (pending.Count > MaxDepth || !_byFrn.TryGetValue(p, out int j) || (_flags[j] & Deleted) != 0)
                {
                    inherited = Orphan;
                    break;
                }
                if (state[j] != 0) { inherited = (byte)(state[j] & Inherited); break; }
                current = j;
            }

            // Se desapila del ancestro mas alto hacia abajo, heredando las marcas
            while (pending.Count > 0)
            {
                int k = pending.Pop();
                if ((_attributes[k] & (uint)ProtectedAttributes) == (uint)ProtectedAttributes) inherited |= SystemTree;
                state[k] = (byte)(inherited | Computed);
            }
        }

        for (int i = 0; i < state.Length; i++)
            _flags[i] = (byte)((_flags[i] & Deleted) | (state[i] & Inherited));
        _flagsDirty = false;
    }

    public string GetName(int entry)
    {
        _lock.EnterReadLock();
        try { return _names[entry]; }
        finally { _lock.ExitReadLock(); }
    }

    public FileAttributes GetAttributes(int entry)
    {
        _lock.EnterReadLock();
        try { return (FileAttributes)_attributes[entry]; }
        finally { _lock.ExitReadLock(); }
    }

    public bool IsDirectory(int entry) => (GetAttributes(entry) & FileAttributes.Directory) != 0;

    public string GetFullPath(int entry)
    {
        _lock.EnterReadLock();
        try { return BuildPath(_parent[entry], _names[entry]); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>Ruta de la carpeta que contiene la entrada.</summary>
    public string GetDirectoryPath(int entry)
    {
        _lock.EnterReadLock();
        try { return BuildPath(_parent[entry], null); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// Tamano y fecha de modificacion. La MFT enumerada no los trae, asi que se piden al sistema la
    /// primera vez y quedan en cache hasta que el journal indique que la entrada cambio.
    /// </summary>
    public FileDetails GetDetails(int entry)
    {
        string path;
        int generation;
        _lock.EnterReadLock();
        try
        {
            if (_modified[entry] != Unknown) return ToDetails(_size[entry], _modified[entry]);
            path = BuildPath(_parent[entry], _names[entry]);
            generation = Generation;
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Fuera del lock: es una llamada al sistema de archivos
        long size, modified;
        if (NativeMethods.GetFileAttributesEx(@"\\?\" + path, 0, out var data))
        {
            size = (data.FileAttributes & (uint)FileAttributes.Directory) != 0
                ? NoSize
                : ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
            modified = DateTime.FromFileTimeUtc(data.LastWriteTime).Ticks;
        }
        else
        {
            size = Unavailable;
            modified = Unavailable;
        }

        _lock.EnterReadLock();
        try
        {
            // Escribir un elemento no cambia la estructura de las listas, basta el lock de lectura
            if (Generation == generation && entry < _modified.Count)
            {
                _size[entry] = size;
                _modified[entry] = modified;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
        return ToDetails(size, modified);
    }

    static FileDetails ToDetails(long size, long modified) => new(
        size >= 0 ? size : null,
        modified == Unavailable ? null : new DateTime(modified, DateTimeKind.Utc).ToLocalTime());

    string BuildPath(ulong p, string? leaf)
    {
        var chain = new Stack<string>();
        if (leaf is not null) chain.Push(leaf);
        for (int depth = 0; p != RootFrn && depth < MaxDepth; depth++)
        {
            if (!_byFrn.TryGetValue(p, out int j))
            {
                chain.Push("?");
                break;
            }
            chain.Push(_names[j]);
            p = _parent[j];
        }

        var sb = new StringBuilder().Append(Drive).Append(":\\");
        sb.AppendJoin('\\', chain);
        return sb.ToString();
    }

    bool IsDirectoryCore(int entry) => (_attributes[entry] & (uint)FileAttributes.Directory) != 0;

    /// <summary>Prepara una busqueda limitada a una carpeta (ruta sin letra de unidad; vacia = todo el volumen).</summary>
    /// <exception cref="DirectoryNotFoundException">La carpeta no existe en el indice.</exception>
    internal SearchScope CreateScope(string relativePath)
    {
        _lock.EnterReadLock();
        try
        {
            if (relativePath.Length == 0)
                return new SearchScope($@"{Drive}:\", this, relativePath);

            int entry = FindDirectory(relativePath);
            if (entry < 0)
                throw new DirectoryNotFoundException($@"No existe la carpeta: {Drive}:\{relativePath}");

            var scope = new SearchScope(BuildPath(_parent[entry], _names[entry]), this, relativePath);
            scope.Cache = new SearchScope.MaskCache(Generation, Version, entry, GetSubtreeMask(entry));
            return scope;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Busca una carpeta por su ruta relativa a la raiz del volumen. Devuelve -1 si no existe.</summary>
    int FindDirectory(string relativePath)
    {
        ulong current = RootFrn;
        int entry = -1;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            entry = -1;
            for (int i = 0; i < _names.Count; i++)
            {
                if (_parent[i] == current && IsDirectoryCore(i) && (_flags[i] & Deleted) == 0 &&
                    string.Equals(_names[i], part, StringComparison.OrdinalIgnoreCase))
                {
                    entry = i;
                    break;
                }
            }
            if (entry < 0) return -1;
            current = _frn[entry];
        }
        return entry;
    }

    /// <summary>
    /// Marca las entradas que estan dentro de la carpeta indicada (a cualquier profundidad).
    /// Recorre cada cadena de padres una sola vez gracias a la memoizacion: O(n) en total.
    /// </summary>
    bool[] GetSubtreeMask(int rootEntry)
    {
        ulong rootFrn = _frn[rootEntry];
        var state = new sbyte[_names.Count]; // 0 = sin calcular, 1 = dentro, -1 = fuera
        var pending = new Stack<int>();

        for (int i = 0; i < state.Length; i++)
        {
            if (state[i] != 0) continue;

            int current = i;
            sbyte result;
            while (true)
            {
                pending.Push(current);
                ulong p = _parent[current];
                if (p == rootFrn) { result = 1; break; }
                if (p == RootFrn || pending.Count > MaxDepth || !_byFrn.TryGetValue(p, out int j)) { result = -1; break; }
                if (state[j] != 0) { result = state[j]; break; }
                current = j;
            }
            while (pending.Count > 0) state[pending.Pop()] = result;
        }

        var mask = new bool[state.Length];
        for (int i = 0; i < state.Length; i++) mask[i] = state[i] == 1;
        return mask;
    }

    /// <summary>Mascara vigente de la carpeta raiz; se recalcula si el indice cambio desde la ultima vez.</summary>
    SearchScope.MaskCache GetScopeCache(SearchScope scope)
    {
        var cache = scope.Cache;
        if (cache is not null && cache.Generation == Generation && cache.Version == Version)
            return cache;

        // Tras volver a enumerar la MFT los indices cambian: hay que volver a ubicar la carpeta
        int entry = cache is not null && cache.Generation == Generation ? cache.RootEntry : FindDirectory(scope.RelativePath);
        var mask = entry >= 0 && (_flags[entry] & Deleted) == 0 ? GetSubtreeMask(entry) : new bool[_names.Count];
        cache = new SearchScope.MaskCache(Generation, Version, entry, mask);
        scope.Cache = cache;
        return cache;
    }

    internal void Search(string text, EntryKind kind, SearchScope? scope, bool includeSystem, List<SearchResult> results,
        CancellationToken cancellationToken)
    {
        _lock.EnterReadLock();
        try
        {
            bool[]? mask = null;
            if (scope is not null && scope.RelativePath.Length > 0)
            {
                var cache = GetScopeCache(scope);
                mask = cache.Mask;
                // Si la raiz ya es de sistema, su contenido se muestra igual
                if (cache.RootEntry >= 0 && (_flags[cache.RootEntry] & SystemTree) != 0) includeSystem = true;
            }

            byte hidden = includeSystem ? (byte)(Deleted | Orphan) : (byte)(Deleted | Orphan | SystemTree);
            for (int i = 0; i < _names.Count; i++)
            {
                if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                if ((_flags[i] & hidden) != 0) continue;
                if (mask is not null && !mask[i]) continue;
                if (kind != EntryKind.All && IsDirectoryCore(i) != (kind == EntryKind.Directories)) continue;
                if (text.Length > 0 && !_names[i].Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(new SearchResult(this, i));
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        _volume.Dispose();
        _lock.Dispose();
    }
}
