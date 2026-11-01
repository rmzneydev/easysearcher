using EasySearcher.Core;
using static EasySearcher.Tests.TestVolume;

namespace EasySearcher.Tests;

public class FileIndexTests
{
    [Fact]
    public void Counts_every_entry_except_the_root()
    {
        Assert.Equal(10, Build().Count);
    }

    [Fact]
    public void Builds_full_paths_from_parents()
    {
        var index = BuildIndex();
        var numb = index.Search("numb").Single();
        Assert.Equal(@"D:\Musica\Linkin Park - Numb.mp3", numb.FullPath);
        Assert.Equal(@"D:\Musica", numb.DirectoryPath);

        var musica = index.Search("musica").Single();
        Assert.Equal(@"D:\", musica.DirectoryPath);
        Assert.True(musica.IsDirectory);
    }

    [Fact]
    public void Hides_ntfs_metafiles_and_protected_system_files_by_default()
    {
        var names = Names(BuildIndex().Search(""));
        Assert.DoesNotContain("$TxfLog.blf", names);
        Assert.DoesNotContain("System Volume Information", names);
        Assert.DoesNotContain("tracking.log", names);   // hereda lo de sistema de su carpeta
        Assert.Contains("notas.txt", names);
    }

    [Fact]
    public void IncludeSystem_shows_system_files_but_never_metafiles()
    {
        var names = Names(BuildIndex().Search("", new SearchOptions { IncludeSystem = true }));
        Assert.Contains("tracking.log", names);
        Assert.DoesNotContain("$TxfLog.blf", names);
    }

    [Theory]
    [InlineData(EntryKind.Directories, new[] { "Docs", "Live", "Musica" })]
    [InlineData(EntryKind.Images, new[] { "cover.JPG" })]
    [InlineData(EntryKind.Audio, new[] { "Linkin Park - Numb.mp3" })]
    [InlineData(EntryKind.Videos, new[] { "Linkin Park - Live.mp4" })]
    [InlineData(EntryKind.Documents, new[] { "notas.txt" })]
    public void Filters_by_kind(EntryKind kind, string[] expected)
    {
        Assert.Equal(expected, Names(BuildIndex().Search("", new SearchOptions { Kind = kind })));
    }

    [Fact]
    public void Query_syntax_applies_to_index_search()
    {
        var index = BuildIndex();
        Assert.Equal(["Linkin Park - Live.mp4", "Linkin Park - Numb.mp3"], Names(index.Search("linkin park")));
        Assert.Equal(["Linkin Park - Numb.mp3"], Names(index.Search("linkin *.mp3")));
        Assert.Equal(["Linkin Park - Live.mp4"], Names(index.Search("linkin !numb")));
    }

    [Fact]
    public void Scope_limits_search_to_the_folder_and_its_subfolders()
    {
        var index = BuildIndex();
        var scope = index.CreateScope(@"d:\musica");   // se normaliza a como esta en el indice
        Assert.Equal(@"D:\Musica", scope.RootPath);

        var names = Names(index.Search("", new SearchOptions { Scope = scope }));
        Assert.Equal(["cover.JPG", "Linkin Park - Live.mp4", "Linkin Park - Numb.mp3", "Live"], names);
    }

    [Fact]
    public void Scope_on_a_system_folder_shows_its_contents()
    {
        var index = BuildIndex();
        var scope = index.CreateScope(@"D:\System Volume Information");
        Assert.Equal(["tracking.log"], Names(index.Search("", new SearchOptions { Scope = scope })));
    }

    [Theory]
    [InlineData(@"D:\NoExiste")]
    [InlineData(@"D:\Musica\cover.JPG")]   // es un archivo, no una carpeta
    [InlineData(@"Z:\")]                    // unidad no indexada
    public void Invalid_scopes_throw(string root)
    {
        Assert.Throws<DirectoryNotFoundException>(() => BuildIndex().CreateScope(root));
    }
}
