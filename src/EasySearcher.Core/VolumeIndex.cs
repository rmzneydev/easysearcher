using System.Diagnostics;
using System.Text;

namespace EasySearcher.Core;

/// <summary>
/// Indice en memoria de todas las entradas (archivos y carpetas) de un volumen NTFS.
/// Se guarda como arreglos paralelos para ocupar poca memoria.
/// </summary>
public sealed class VolumeIndex
{
    const ulong RootFrn = 5;
    const int MaxDepth = 512;

    // Marcas por entrada, calculadas al construir el indice
    const byte Orphan = 0x1;      // su cadena de padres no llega a la raiz: metarchivos de NTFS ($Extend, ...)
    const byte SystemTree = 0x2;  // ella o alguna carpeta que la contiene es oculta + sistema
    const byte Computed = 0x80;
    const FileAttributes ProtectedAttributes = FileAttributes.Hidden | FileAttributes.System;

    readonly ulong[] _frn;
    readonly ulong[] _parent;
    readonly uint[] _attributes;
    readonly string[] _names;
    readonly Dictionary<ulong, int> _byFrn;
    readonly byte[] _flags;

    public char Drive { get; }
    public int Count => _names.Length;
    public TimeSpan BuildTime { get; }

    VolumeIndex(char drive, ulong[] frn, ulong[] parent, uint[] attributes, string[] names, TimeSpan buildTime)
    {
        Drive = drive;
        _frn = frn;
        _parent = parent;
        _attributes = attributes;
        _names = names;
        BuildTime = buildTime;

        _byFrn = new Dictionary<ulong, int>(frn.Length);
        for (int i = 0; i < frn.Length; i++)
            _byFrn[frn[i]] = i;

        _flags = ComputeFlags();
    }

    /// <summary>Propaga Orphan y SystemTree de padres a hijos; cada cadena se recorre una sola vez.</summary>
    byte[] ComputeFlags()
    {
        var flags = new byte[_names.Length];
        var pending = new Stack<int>();

        for (int i = 0; i < flags.Length; i++)
        {
            if (flags[i] != 0) continue;

            int current = i;
            byte inherited;
            while (true)
            {
                pending.Push(current);
                ulong p = _parent[current];
                if (p == RootFrn) { inherited = 0; break; }
                if (pending.Count > MaxDepth || !_byFrn.TryGetValue(p, out int j)) { inherited = Orphan; break; }
                if (flags[j] != 0) { inherited = (byte)(flags[j] & ~Computed); break; }
                current = j;
            }

            // Se desapila del ancestro mas alto hacia abajo, heredando las marcas
            while (pending.Count > 0)
            {
                int k = pending.Pop();
                if ((GetAttributes(k) & ProtectedAttributes) == ProtectedAttributes) inherited |= SystemTree;
                flags[k] = (byte)(inherited | Computed);
            }
        }

        for (int i = 0; i < flags.Length; i++) flags[i] &= unchecked((byte)~Computed);
        return flags;
    }

    internal bool IsSystem(int entry) => (_flags[entry] & SystemTree) != 0;

    public static VolumeIndex Build(char drive)
    {
        drive = char.ToUpperInvariant(drive);
        var sw = Stopwatch.StartNew();

        var frn = new List<ulong>(1 << 16);
        var parent = new List<ulong>(1 << 16);
        var attributes = new List<uint>(1 << 16);
        var names = new List<string>(1 << 16);

        MftReader.Enumerate(drive, (f, p, a, name) =>
        {
            if (f == RootFrn) return;
            frn.Add(f);
            parent.Add(p);
            attributes.Add(a);
            names.Add(new string(name));
        });

        return new VolumeIndex(drive, frn.ToArray(), parent.ToArray(), attributes.ToArray(), names.ToArray(), sw.Elapsed);
    }

    public string GetName(int entry) => _names[entry];

    public FileAttributes GetAttributes(int entry) => (FileAttributes)_attributes[entry];

    public bool IsDirectory(int entry) => (GetAttributes(entry) & FileAttributes.Directory) != 0;

    public string GetFullPath(int entry) => BuildPath(_parent[entry], _names[entry]);

    /// <summary>Ruta de la carpeta que contiene la entrada.</summary>
    public string GetDirectoryPath(int entry) => BuildPath(_parent[entry], null);

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

    /// <summary>Busca una carpeta por su ruta relativa a la raiz del volumen (sin letra de unidad). Devuelve -1 si no existe.</summary>
    internal int FindDirectory(string relativePath)
    {
        ulong current = RootFrn;
        int entry = -1;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            entry = -1;
            for (int i = 0; i < _names.Length; i++)
            {
                if (_parent[i] == current && IsDirectory(i) && string.Equals(_names[i], part, StringComparison.OrdinalIgnoreCase))
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
    internal bool[] GetSubtreeMask(int rootEntry)
    {
        ulong rootFrn = _frn[rootEntry];
        var state = new sbyte[_names.Length]; // 0 = sin calcular, 1 = dentro, -1 = fuera
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

    internal void Search(string text, EntryKind kind, bool[]? mask, bool includeSystem, List<SearchResult> results,
        CancellationToken cancellationToken)
    {
        byte hidden = includeSystem ? Orphan : (byte)(Orphan | SystemTree);
        for (int i = 0; i < _names.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if ((_flags[i] & hidden) != 0) continue;
            if (mask is not null && !mask[i]) continue;
            if (kind != EntryKind.All && IsDirectory(i) != (kind == EntryKind.Directories)) continue;
            if (text.Length > 0 && !_names[i].Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(new SearchResult(this, i));
        }
    }
}
