using EasySearcher.App;
using EasySearcher.Core;

namespace EasySearcher.Tests;

public class SearchHistoryTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"easysearcher-history-{Guid.NewGuid():N}.json");
    static readonly DateTime T0 = new(2026, 11, 1, 18, 0, 0);

    public void Dispose() => File.Delete(_path);

    static SearchState Entry(string text, DateTime used, string? root = @"D:\Musica",
        EntryKind kind = EntryKind.All, DatePreset date = DatePreset.Any, SizeCriterion size = default) =>
        new(text, root, kind, date, null, null, size) { LastUsed = used };

    [Fact]
    public void Newest_first_and_repeated_searches_move_to_the_top()
    {
        var history = SearchHistory.Load(_path);
        history.Add(Entry("linkin", T0));
        history.Add(Entry("numb", T0.AddHours(1)));
        history.Add(Entry("LINKIN", T0.AddHours(2)));   // misma busqueda, sin distinguir mayusculas

        Assert.Equal(["LINKIN", "numb"], history.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Refining_a_search_soon_after_replaces_the_shorter_one()
    {
        var history = SearchHistory.Load(_path);
        history.Add(Entry("linkin", T0));
        history.Add(Entry("linkin park", T0.AddMinutes(1)));
        Assert.Equal(["linkin park"], history.Entries.Select(e => e.Text));
    }

    [Fact]
    public void Old_or_different_context_searches_are_kept()
    {
        var history = SearchHistory.Load(_path);
        history.Add(Entry("linkin", T0));
        history.Add(Entry("linkin park", T0.AddHours(1)));                // paso mucho tiempo
        history.Add(Entry("linkin park live", T0.AddHours(1).AddMinutes(1), root: @"E:\"));  // otra carpeta
        Assert.Equal(3, history.Entries.Count);
    }

    [Fact]
    public void The_same_text_with_other_filters_is_another_search()
    {
        var history = SearchHistory.Load(_path);
        history.Add(Entry("*.mp4", T0, kind: EntryKind.Videos));
        history.Add(Entry("*.mp4", T0.AddMinutes(1), size: new SizeCriterion(SizeMode.Larger, 4, SizeUnit.GB)));
        Assert.Equal(2, history.Entries.Count);
    }

    [Fact]
    public void Keeps_at_most_fifty()
    {
        var history = SearchHistory.Load(_path);
        for (int i = 0; i < 60; i++) history.Add(Entry($"busqueda {i:00}", T0.AddHours(i)));
        Assert.Equal(50, history.Entries.Count);
        Assert.Equal("busqueda 59", history.Entries[0].Text);
    }

    [Fact]
    public void Matching_filters_by_text()
    {
        var history = SearchHistory.Load(_path);
        history.Add(Entry("linkin park", T0));
        history.Add(Entry("*.pdf", T0.AddHours(1)));
        Assert.Equal(["linkin park"], history.Matching("PARK").Select(e => e.Text));
        Assert.Equal(2, history.Matching("").Count);
    }

    [Fact]
    public void Survives_a_restart_with_its_whole_context()
    {
        var history = SearchHistory.Load(_path);
        history.Add(new SearchState("*.jpg", @"D:\Fotos", EntryKind.Images, DatePreset.Custom,
            new DateTime(2024, 1, 1), new DateTime(2024, 12, 31), new SizeCriterion(SizeMode.Smaller, 128, SizeUnit.MB)) { LastUsed = T0 });

        var reloaded = SearchHistory.Load(_path).Entries.Single();
        Assert.Equal(@"D:\Fotos", reloaded.Root);
        Assert.Equal(EntryKind.Images, reloaded.Kind);
        Assert.Equal(DatePreset.Custom, reloaded.Date);
        Assert.Equal(new DateTime(2024, 12, 31), reloaded.CustomTo);
        Assert.Equal(new SizeCriterion(SizeMode.Smaller, 128, SizeUnit.MB), reloaded.Size);
        // El formato de las fechas depende de la configuracion regional: se comprueba el resto
        Assert.StartsWith(@"en D:\Fotos  ·  imágenes  ·  modificados del ", reloaded.Description);
        Assert.EndsWith("  ·  de menos de 128 MB", reloaded.Description);
    }

    // Antes "Size" era un numero de categoria: 0 cualquiera, 1 vacio, 4 mediano, 7 gigantesco
    [Theory]
    [InlineData(0, (int)SizeMode.Any, 0, (int)SizeUnit.KB)]
    [InlineData(1, (int)SizeMode.Empty, 0, (int)SizeUnit.KB)]
    [InlineData(4, (int)SizeMode.Smaller, 128, (int)SizeUnit.MB)]
    [InlineData(7, (int)SizeMode.Larger, 4, (int)SizeUnit.GB)]
    public void Entries_saved_with_the_old_size_categories_still_load(int category, int mode, double value, int unit)
    {
        File.WriteAllText(_path, $$"""
            [{ "Text": "*.mp4", "Root": null, "Kind": 0, "Date": 0, "CustomFrom": null, "CustomTo": null,
               "Size": {{category}}, "LastUsed": "2026-11-01T18:00:00" }]
            """);

        var size = SearchHistory.Load(_path).Entries.Single().Size;
        Assert.Equal(new SizeCriterion((SizeMode)mode, value, (SizeUnit)unit), size);
    }

    [Fact]
    public void Clear_and_remove_persist()
    {
        var history = SearchHistory.Load(_path);
        history.Add(Entry("a", T0));
        history.Add(Entry("b", T0.AddHours(1)));
        history.Remove(history.Entries[0]);
        Assert.Equal(["a"], SearchHistory.Load(_path).Entries.Select(e => e.Text));
        history.Clear();
        Assert.Empty(SearchHistory.Load(_path).Entries);
    }
}
