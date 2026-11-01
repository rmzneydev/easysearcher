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

    readonly ulong[] _frn;
    readonly ulong[] _parent;
    readonly uint[] _attributes;
    readonly string[] _names;
    readonly Dictionary<ulong, int> _byFrn;

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
    }

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

    public string GetFullPath(int entry)
    {
        var chain = new Stack<string>();
        chain.Push(_names[entry]);
        ulong p = _parent[entry];
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

    internal void Search(string text, EntryKind kind, List<SearchResult> results, CancellationToken cancellationToken)
    {
        for (int i = 0; i < _names.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (kind != EntryKind.All && IsDirectory(i) != (kind == EntryKind.Directories)) continue;
            if (text.Length > 0 && !_names[i].Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(new SearchResult(this, i));
        }
    }
}
