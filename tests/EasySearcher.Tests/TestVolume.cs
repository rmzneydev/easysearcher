using EasySearcher.Core;

namespace EasySearcher.Tests;

/// <summary>
/// Volumen de prueba armado en memoria (sin leer la MFT):
///
///   D:\
///   ├── Musica\                          frn 100
///   │   ├── Linkin Park - Numb.mp3       frn 101
///   │   ├── cover.JPG                    frn 102
///   │   └── Live\                        frn 103
///   │       └── Linkin Park - Live.mp4   frn 104
///   ├── Docs\                            frn 200
///   │   └── notas.txt                    frn 201
///   ├── System Volume Information\       frn 300 (oculta + sistema)
///   │   └── tracking.log                 frn 301
///   └── (padre 11 = $Extend, no existe)
///       └── $TxfLog.blf                  frn 400 (metarchivo de NTFS, huerfano)
/// </summary>
static class TestVolume
{
    public const uint DirAttr = 0x10;
    public const uint FileAttr = 0x20;
    public const uint HiddenSystemDir = DirAttr | 0x02 | 0x04;
    public const ulong Root = 5;

    public static JournalRecord Rec(ulong frn, ulong parent, uint attributes, string name, uint reason = 0) =>
        new(frn, parent, attributes, reason, name);

    public static VolumeIndex Build() => VolumeIndex.FromRecords('d',
    [
        Rec(Root, Root, DirAttr, "."),
        Rec(100, Root, DirAttr, "Musica"),
        Rec(101, 100, FileAttr, "Linkin Park - Numb.mp3"),
        Rec(102, 100, FileAttr, "cover.JPG"),
        Rec(103, 100, DirAttr, "Live"),
        Rec(104, 103, FileAttr, "Linkin Park - Live.mp4"),
        Rec(200, Root, DirAttr, "Docs"),
        Rec(201, 200, FileAttr, "notas.txt"),
        Rec(300, Root, HiddenSystemDir, "System Volume Information"),
        Rec(301, 300, FileAttr, "tracking.log"),
        Rec(400, 11, FileAttr, "$TxfLog.blf"),
    ]);

    public static FileIndex BuildIndex() => new([Build()], new Dictionary<char, Exception>());

    public static string[] Names(IEnumerable<SearchResult> results) => results.Select(r => r.Name).Order().ToArray();
}
