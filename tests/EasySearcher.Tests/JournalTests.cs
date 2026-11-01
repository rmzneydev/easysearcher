using EasySearcher.Core;
using static EasySearcher.Tests.TestVolume;

namespace EasySearcher.Tests;

/// <summary>Cambios del USN Journal aplicados al indice (ApplyRecords), sin leer el journal real.</summary>
public class JournalTests
{
    const uint Create = 0x100;
    const uint Delete = 0x200;
    const uint RenameOld = 0x1000;
    const uint RenameNew = 0x2000;
    const uint DataExtend = 0x2;
    const uint BasicInfo = 0x8000;

    readonly VolumeIndex _volume = Build();
    readonly FileIndex _index;
    readonly SearchScope _musica;

    public JournalTests()
    {
        _index = new FileIndex([_volume], new Dictionary<char, Exception>());
        _musica = _index.CreateScope(@"D:\Musica");
    }

    string[] InMusica(string text = "") => Names(_index.Search(text, new SearchOptions { Scope = _musica }));

    [Fact]
    public void New_file_appears_including_in_an_existing_scope()
    {
        Assert.Equal(IndexChange.Structure, _volume.ApplyRecords([Rec(500, 103, FileAttr, "In The End.mp3", Create)]));
        Assert.Contains("In The End.mp3", InMusica());
        Assert.Equal(@"D:\Musica\Live\In The End.mp3", _index.Search("in the end").Single().FullPath);
    }

    [Fact]
    public void Rename_keeps_the_entry_with_its_new_name()
    {
        _volume.ApplyRecords(
        [
            Rec(201, 200, FileAttr, "notas.txt", RenameOld),
            Rec(201, 200, FileAttr, "apuntes.txt", RenameNew),
        ]);
        Assert.Empty(_index.Search("notas"));
        Assert.Equal(@"D:\Docs\apuntes.txt", _index.Search("apuntes").Single().FullPath);
    }

    [Fact]
    public void Moving_out_of_the_scope_removes_it_from_scoped_results()
    {
        _volume.ApplyRecords([Rec(102, 200, FileAttr, "cover.JPG", RenameNew)]);
        Assert.DoesNotContain("cover.JPG", InMusica());
        Assert.Equal(@"D:\Docs\cover.JPG", _index.Search("cover").Single().FullPath);
    }

    [Fact]
    public void Deleted_entries_disappear_and_count_decreases()
    {
        int before = _volume.Count;
        Assert.Equal(IndexChange.Structure, _volume.ApplyRecords([Rec(101, 100, FileAttr, "Linkin Park - Numb.mp3", Delete)]));
        Assert.Empty(_index.Search("numb"));
        Assert.Equal(before - 1, _volume.Count);
    }

    [Fact]
    public void A_reused_record_number_becomes_a_new_entry()
    {
        _volume.ApplyRecords([Rec(201, 200, FileAttr, "notas.txt", Delete)]);
        _volume.ApplyRecords([Rec(201, 100, FileAttr, "nueva.mp3", Create)]);
        Assert.Empty(_index.Search("notas"));
        Assert.Contains("nueva.mp3", InMusica());
    }

    [Fact]
    public void Moving_a_folder_into_a_system_folder_hides_its_contents()
    {
        _volume.ApplyRecords([Rec(103, 300, DirAttr, "Live", RenameNew)]);
        Assert.Empty(_index.Search("live"));
        Assert.Equal(2, _index.Search("live", new SearchOptions { IncludeSystem = true }).Count);
    }

    [Fact]
    public void Making_a_folder_hidden_and_system_hides_its_contents()
    {
        _volume.ApplyRecords([Rec(200, Root, HiddenSystemDir, "Docs", BasicInfo)]);
        Assert.Empty(_index.Search("notas"));
    }

    [Fact]
    public void Content_only_changes_report_details_not_structure()
    {
        Assert.Equal(IndexChange.Details, _volume.ApplyRecords([Rec(201, 200, FileAttr, "notas.txt", DataExtend)]));
        Assert.Single(_index.Search("notas"));
    }

    [Fact]
    public void A_batch_can_report_both_kinds_of_change()
    {
        var change = _volume.ApplyRecords(
        [
            Rec(201, 200, FileAttr, "notas.txt", DataExtend),
            Rec(500, 200, FileAttr, "nuevo.txt", Create),
        ]);
        Assert.Equal(IndexChange.Structure | IndexChange.Details, change);
    }

    [Fact]
    public void Changes_to_unknown_entries_are_ignored()
    {
        Assert.Equal(IndexChange.None, _volume.ApplyRecords([Rec(999, 200, FileAttr, "fantasma.txt", DataExtend)]));
        Assert.Equal(IndexChange.None, _volume.ApplyRecords([Rec(999, 200, FileAttr, "fantasma.txt", Delete)]));
    }
}
