using EasySearcher.Core;
using static EasySearcher.Tests.TestVolume;

namespace EasySearcher.Tests;

public class ResultSorterTests
{
    [Fact]
    public void Sorts_by_name_case_insensitive()
    {
        var results = BuildIndex().Search("", new SearchOptions { Kind = EntryKind.Files });
        ResultSorter.Sort(results, SortColumn.Name, descending: false);
        Assert.Equal(["cover.JPG", "Linkin Park - Live.mp4", "Linkin Park - Numb.mp3", "notas.txt"],
            results.Select(r => r.Name));
    }

    [Fact]
    public void Descending_reverses_the_order()
    {
        var results = BuildIndex().Search("", new SearchOptions { Kind = EntryKind.Files });
        ResultSorter.Sort(results, SortColumn.Name, descending: true);
        Assert.Equal("notas.txt", results[0].Name);
        Assert.Equal("cover.JPG", results[^1].Name);
    }

    [Fact]
    public void Sorts_by_folder_then_name()
    {
        var results = BuildIndex().Search("", new SearchOptions { Kind = EntryKind.Files });
        ResultSorter.Sort(results, SortColumn.Folder, descending: false);
        Assert.Equal(
            [@"D:\Docs\notas.txt", @"D:\Musica\cover.JPG", @"D:\Musica\Linkin Park - Numb.mp3", @"D:\Musica\Live\Linkin Park - Live.mp4"],
            results.Select(r => r.FullPath));
    }

    [Theory]
    [InlineData("Árbol.txt", "Arbol.txt")]
    [InlineData("Ñandú", "Nandu")]
    [InlineData("ÉXITO", "EXITO")]
    [InlineData("plain ascii.txt", "plain ascii.txt")]
    public void Sort_keys_ignore_accents(string name, string key)
    {
        Assert.Equal(key, ResultSorter.SortKey(name));
    }

    [Fact]
    public void Accented_names_sort_next_to_their_letter()
    {
        var volume = VolumeIndex.FromRecords('d',
        [
            Rec(Root, Root, DirAttr, "."),
            Rec(10, Root, FileAttr, "Zeta.txt"),
            Rec(11, Root, FileAttr, "Árbol.txt"),
            Rec(12, Root, FileAttr, "Barco.txt"),
        ]);
        var results = new FileIndex([volume], new Dictionary<char, Exception>()).Search("");
        ResultSorter.Sort(results, SortColumn.Name, descending: false);
        Assert.Equal(["Árbol.txt", "Barco.txt", "Zeta.txt"], results.Select(r => r.Name));
    }

    [Fact]
    public void Sorting_the_same_results_twice_gives_the_same_order()
    {
        var a = BuildIndex().Search("");
        var b = a.ToList();
        b.Reverse();
        ResultSorter.Sort(a, SortColumn.Folder, descending: false);
        ResultSorter.Sort(b, SortColumn.Folder, descending: false);
        Assert.Equal(a.Select(r => r.Entry), b.Select(r => r.Entry));
    }
}
