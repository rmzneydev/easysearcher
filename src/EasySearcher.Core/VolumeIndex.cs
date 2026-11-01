using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EasySearcher.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace EasySearcher.Core;

/// <summary>
/// Indice en memoria de todas las entradas (archivos y carpetas) de un volumen NTFS, que se mantiene
/// al dia leyendo el USN Journal con <see cref="Update"/>.
/// Las listas paralelas se indexan por el numero de registro de la MFT (el FRN sin numero de secuencia):
/// asi no hace falta un diccionario para encontrar al padre, y los indices que ya tiene la interfaz
/// siguen siendo validos aunque se borren entradas.
/// </summary>
public sealed class VolumeIndex : IDisposable
{
    const uint RootFrn = 5;
    const int MaxDepth = 512;

    // Marcas por entrada
    const byte Orphan = 0x1;      // su cadena de padres no llega a la raiz: metarchivos de NTFS ($Extend, ...)
    const byte SystemTree = 0x2;  // ella o alguna carpeta que la contiene es oculta + sistema
    const byte Empty = 0x4;       // registro de la MFT sin entrada (libre o borrada)
    const byte Inherited = Orphan | SystemTree;
    const byte Computed = 0x80;
    const FileAttributes ProtectedAttributes = FileAttributes.Hidden | FileAttributes.System;

    // Valores especiales de la cache de tamano/fecha
    const long Unavailable = long.MinValue + 1;  // no se pudo leer (sin permisos, ya no existe...)
    const long NoSize = -1;                      // carpetas

    readonly ReaderWriterLockSlim _lock = new();
    readonly SafeFileHandle? _volume; // null en indices construidos con FromRecords

    List<uint> _parent = [];
    List<uint> _attributes = [];
    List<string> _names = [];
    List<byte> _flags = [];
    DetailsCache _details = new();
    int _count;
    bool _flagsDirty;

    // Mientras se lee el tamano/fecha de la $MFT, entradas que el journal cambio (sus datos leidos ya son viejos)
    HashSet<int>? _changedDuringDetailsLoad;

    JournalInfo? _journal;
    long _nextUsn;

    /// <summary>Cambia cada vez que se vuelve a enumerar la MFT.</summary>
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
            try { return _count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    VolumeIndex(char drive, SafeFileHandle? volume)
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

    /// <summary>
    /// Indice construido a partir de registros en memoria, sin leer la MFT ni el journal (no requiere
    /// administrador). Para pruebas.
    /// </summary>
    internal static VolumeIndex FromRecords(char drive, IEnumerable<JournalRecord> records)
    {
        var index = new VolumeIndex(char.ToUpperInvariant(drive), null);
        index.Populate(onRecord =>
        {
            foreach (var r in records)
                onRecord(r.Frn, r.Parent, r.Attributes, 0, r.Name);
        }, journal: null);
        return index;
    }

    /// <summary>Enumera la MFT completa y reemplaza el contenido del indice.</summary>
    void Load()
    {
        var sw = Stopwatch.StartNew();
        var volume = _volume ?? throw new InvalidOperationException("Indice sin volumen asociado.");

        // Se toma la posicion del journal antes de enumerar para no perder cambios ocurridos mientras tanto
        var journal = MftReader.QueryJournal(volume, Drive);
        Populate(onRecord => MftReader.Enumerate(volume, Drive, onRecord), journal);
        BuildTime = sw.Elapsed;
    }

    /// <summary>Reemplaza el contenido del indice con los registros que entrega <paramref name="enumerate"/>.</summary>
    void Populate(Action<MftReader.RecordHandler> enumerate, JournalInfo? journal)
    {
        var parent = new List<uint>(1 << 16);
        var attributes = new List<uint>(1 << 16);
        var names = new List<string>(1 << 16);
        var flags = new List<byte>(1 << 16);
        int count = 0;

        // Muchos nombres se repiten (index.js, LICENSE, ...): se guarda un solo string por nombre distinto
        var pool = new HashSet<string>(StringComparer.Ordinal);
        var lookup = pool.GetAlternateLookup<ReadOnlySpan<char>>();

        enumerate((frn, p, a, _, name) =>
        {
            if (frn == RootFrn || frn > int.MaxValue) return;
            int slot = (int)frn;
            while (names.Count <= slot)
            {
                parent.Add(0);
                attributes.Add(0);
                names.Add("");
                flags.Add(Empty);
            }

            if (!lookup.TryGetValue(name, out var pooled))
            {
                pooled = new string(name);
                pool.Add(pooled);
            }

            if ((flags[slot] & Empty) != 0) count++;
            parent[slot] = p > uint.MaxValue ? uint.MaxValue : (uint)p;
            attributes[slot] = a;
            names[slot] = pooled;
            flags[slot] = 0;
        });

        parent.TrimExcess();
        attributes.TrimExcess();
        names.TrimExcess();
        flags.TrimExcess();
        var details = new DetailsCache();
        details.EnsureCapacity(names.Count);

        _lock.EnterWriteLock();
        try
        {
            _parent = parent;
            _attributes = attributes;
            _names = names;
            _flags = flags;
            _details = details;
            _count = count;
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
    }

    /// <summary>
    /// Aplica los cambios pendientes del USN Journal e indica que cambio: la estructura (nombres,
    /// ubicaciones, atributos) o solo el contenido (tamano/fecha en cache descartados).
    /// No es seguro llamarlo desde varios hilos a la vez.
    /// </summary>
    public IndexChange Update()
    {
        if (_journal is not { } journal || _volume is not { } volume) return IndexChange.None;

        var records = new List<JournalRecord>();
        long nextUsn = _nextUsn;
        if (!MftReader.ReadJournal(volume, Drive, journal.Id, ref nextUsn, records))
        {
            Load();
            return IndexChange.Structure | IndexChange.Details;
        }
        _nextUsn = nextUsn;
        return ApplyRecords(records);
    }

    /// <summary>Aplica registros del journal ya leidos.</summary>
    internal IndexChange ApplyRecords(IReadOnlyList<JournalRecord> records)
    {
        if (records.Count == 0) return IndexChange.None;

        var change = IndexChange.None;
        _lock.EnterWriteLock();
        try
        {
            foreach (var record in records)
                change |= Apply(record);
            if (_flagsDirty) ComputeFlags();
            if ((change & IndexChange.Structure) != 0) Version++;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        return change;
    }

    IndexChange Apply(in JournalRecord r)
    {
        if (r.Frn == RootFrn || r.Frn > int.MaxValue) return IndexChange.None;
        int i = (int)r.Frn;
        bool exists = Exists(i);
        _changedDuringDetailsLoad?.Add(i);

        if ((r.Reason & MftReader.StructuralReasons) == 0)
        {
            // Solo cambio el contenido: basta con olvidar el tamano y la fecha
            if (!exists) return IndexChange.None;
            _details.Invalidate(i);
            return IndexChange.Details;
        }

        if ((r.Reason & MftReader.ReasonFileDelete) != 0)
        {
            if (!exists) return IndexChange.None;
            // El nombre se conserva para que los resultados ya mostrados no queden en blanco
            _flags[i] |= Empty;
            _count--;
            _details.Invalidate(i);
            return IndexChange.Structure;
        }

        // El registro con el nombre viejo de un renombrado no aporta nada: le sigue el del nombre nuevo
        if ((r.Reason & (MftReader.ReasonRenameOldName | MftReader.ReasonRenameNewName)) == MftReader.ReasonRenameOldName)
            return IndexChange.None;

        uint parent = r.Parent > uint.MaxValue ? uint.MaxValue : (uint)r.Parent;
        if (exists)
        {
            // Si una carpeta se mueve o cambia de oculta/sistema, cambian las marcas de todo su contenido
            bool isDirectory = (r.Attributes & (uint)FileAttributes.Directory) != 0;
            if (isDirectory && (_parent[i] != parent ||
                                ((_attributes[i] ^ r.Attributes) & (uint)ProtectedAttributes) != 0))
                _flagsDirty = true;
        }
        else
        {
            EnsureSlot(i);
            _count++;
        }

        _parent[i] = parent;
        _attributes[i] = r.Attributes;
        _names[i] = r.Name;
        _details.Invalidate(i);
        _flags[i] = ComputeEntryFlags(i);
        return IndexChange.Structure;
    }

    void EnsureSlot(int slot)
    {
        while (_names.Count <= slot)
        {
            _parent.Add(0);
            _attributes.Add(0);
            _names.Add("");
            _flags.Add(Empty);
        }
        _details.EnsureCapacity(_names.Count);
    }

    bool Exists(int entry) => entry < _flags.Count && (_flags[entry] & Empty) == 0;

    /// <summary>Indice del padre si existe en el indice (la raiz del volumen no cuenta).</summary>
    bool TryGetParent(uint parent, out int entry)
    {
        entry = (int)parent;
        return parent < (uint)_flags.Count && (_flags[entry] & Empty) == 0;
    }

    /// <summary>Marcas de una entrada a partir de las de su padre.</summary>
    byte ComputeEntryFlags(int entry)
    {
        byte flags;
        uint p = _parent[entry];
        if (p == RootFrn) flags = 0;
        else if (TryGetParent(p, out int j)) flags = (byte)(_flags[j] & Inherited);
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
            if (state[i] != 0 || (_flags[i] & Empty) != 0) continue;

            int current = i;
            byte inherited;
            while (true)
            {
                pending.Push(current);
                uint p = _parent[current];
                if (p == RootFrn) { inherited = 0; break; }
                if (pending.Count > MaxDepth || !TryGetParent(p, out int j)) { inherited = Orphan; break; }
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
            _flags[i] = (byte)((_flags[i] & Empty) | (state[i] & Inherited));
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
            if (_details.TryGet(entry, out long cachedSize, out long cachedModified))
                return ToDetails(cachedSize, cachedModified);
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
            if (Generation == generation && entry < _names.Count)
                _details.Set(entry, size, modified);
        }
        finally
        {
            _lock.ExitReadLock();
        }
        return ToDetails(size, modified);
    }

    /// <summary>
    /// Lee el tamano y la fecha de todas las entradas de la $MFT cruda y los deja en cache, para que ordenar
    /// y filtrar por tamano o fecha no tengan que consultar el disco archivo por archivo. Tarda unos
    /// segundos; el indice se puede usar mientras tanto. Devuelve cuantas entradas se completaron.
    /// </summary>
    public int LoadDetailsFromMft(CancellationToken cancellationToken = default)
    {
        if (_volume is not { } volume) return 0;

        int slots, generation;
        _lock.EnterWriteLock();
        try
        {
            slots = _names.Count;
            generation = Generation;
            _changedDuringDetailsLoad = [];
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        var sizes = new long[slots];
        var dates = new long[slots];
        Array.Fill(sizes, MftDetailsReader.Unknown);
        Array.Fill(dates, MftDetailsReader.Unknown);
        try
        {
            MftDetailsReader.Read(volume, Drive, (frn, size, modified) =>
            {
                if (frn >= slots) return;
                if (size != MftDetailsReader.Unknown) sizes[frn] = size;
                if (modified != MftDetailsReader.Unknown) dates[frn] = modified;
            }, cancellationToken);
        }
        catch
        {
            _lock.EnterWriteLock();
            try { _changedDuringDetailsLoad = null; }
            finally { _lock.ExitWriteLock(); }
            throw;
        }

        int filled = 0;
        _lock.EnterWriteLock();
        try
        {
            var changed = _changedDuringDetailsLoad!;
            _changedDuringDetailsLoad = null;
            // Si mientras tanto se volvio a enumerar la MFT, los numeros de registro ya no corresponden
            if (Generation != generation) return 0;

            for (int i = 0; i < slots; i++)
            {
                if (dates[i] == MftDetailsReader.Unknown || (_flags[i] & Empty) != 0 || changed.Contains(i)) continue;
                long size = IsDirectoryCore(i) ? NoSize : sizes[i];
                if (size == MftDetailsReader.Unknown) continue;
                _details.Set(i, size, dates[i]);
                filled++;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        return filled;
    }

    static FileDetails ToDetails(long size, long modified) => new(
        size >= 0 ? size : null,
        modified == Unavailable ? null : new DateTime(modified, DateTimeKind.Utc).ToLocalTime());

    string BuildPath(uint p, string? leaf)
    {
        var chain = new Stack<string>();
        if (leaf is not null) chain.Push(leaf);
        for (int depth = 0; p != RootFrn && depth < MaxDepth; depth++)
        {
            if (!TryGetParent(p, out int j))
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
        uint current = RootFrn;
        int entry = -1;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            entry = -1;
            for (int i = 0; i < _names.Count; i++)
            {
                if (_parent[i] == current && (_flags[i] & Empty) == 0 && IsDirectoryCore(i) &&
                    string.Equals(_names[i], part, StringComparison.OrdinalIgnoreCase))
                {
                    entry = i;
                    break;
                }
            }
            if (entry < 0) return -1;
            current = (uint)entry;
        }
        return entry;
    }

    /// <summary>
    /// Marca las entradas que estan dentro de la carpeta indicada (a cualquier profundidad).
    /// Recorre cada cadena de padres una sola vez gracias a la memoizacion: O(n) en total.
    /// </summary>
    bool[] GetSubtreeMask(int rootEntry)
    {
        uint rootFrn = (uint)rootEntry;
        var state = new sbyte[_names.Count]; // 0 = sin calcular, 1 = dentro, -1 = fuera
        var pending = new Stack<int>();

        for (int i = 0; i < state.Length; i++)
        {
            if (state[i] != 0) continue;
            if ((_flags[i] & Empty) != 0)
            {
                state[i] = -1;
                continue;
            }

            int current = i;
            sbyte result;
            while (true)
            {
                pending.Push(current);
                uint p = _parent[current];
                if (p == rootFrn) { result = 1; break; }
                if (p == RootFrn || pending.Count > MaxDepth || !TryGetParent(p, out int j)) { result = -1; break; }
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

        // Tras volver a enumerar la MFT hay que volver a ubicar la carpeta
        int entry = cache is not null && cache.Generation == Generation ? cache.RootEntry : FindDirectory(scope.RelativePath);
        var mask = entry >= 0 && Exists(entry) ? GetSubtreeMask(entry) : new bool[_names.Count];
        cache = new SearchScope.MaskCache(Generation, Version, entry, mask);
        scope.Cache = cache;
        return cache;
    }

    internal void Search(SearchQuery query, EntryKind kind, SearchScope? scope, bool includeSystem, List<SearchResult> results,
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
                if (cache.RootEntry >= 0 && Exists(cache.RootEntry) && (_flags[cache.RootEntry] & SystemTree) != 0)
                    includeSystem = true;
            }

            // Spans en lugar del indexador de List: este es el bucle caliente de la busqueda
            var names = CollectionsMarshal.AsSpan(_names);
            var flags = CollectionsMarshal.AsSpan(_flags);
            var attributes = CollectionsMarshal.AsSpan(_attributes);
            byte hidden = includeSystem ? (byte)(Empty | Orphan) : (byte)(Empty | Orphan | SystemTree);
            bool filterKind = kind != EntryKind.All;
            bool wantDirectories = kind == EntryKind.Directories;
            var category = FileCategories.GetLookup(kind);
            bool filterExtension = category.HasValue;
            var extensions = category.GetValueOrDefault();

            for (int i = 0; i < names.Length; i++)
            {
                if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                if ((flags[i] & hidden) != 0) continue;
                if (mask is not null && !mask[i]) continue;
                if (filterKind && ((attributes[i] & (uint)FileAttributes.Directory) != 0) != wantDirectories) continue;
                if (filterExtension && !extensions.Contains(FileCategories.GetExtension(names[i]))) continue;
                if (!query.IsEmpty && !query.Matches(names[i])) continue;
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
        _volume?.Dispose();
        _lock.Dispose();
    }
}
