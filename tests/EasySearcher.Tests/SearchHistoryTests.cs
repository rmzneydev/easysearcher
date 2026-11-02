using EasySearcher.App;
using EasySearcher.Core;

namespace EasySearcher.Tests;

public class SearchHistoryTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"easysearcher-history-{Guid.NewGuid():N}.json");
    static readonly DateTime T0 = new(2026, 11, 1, 18, 0, 0);

    public void Dispose() => File.Delete(_path);

    static HistoryEntry Entry(string text, DateTime used, string? root = @"D:\Musica",
        EntryKind kind = EntryKind.All, DatePreset date = DatePreset.Any, SizePreset size = SizePreset.Any) =>
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
        history.Add(Entry("*.mp4", T0.AddMinutes(1), size: SizePreset.Gigantic));
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
        history.Add(new HistoryEntry("*.jpg", @"D:\Fotos", EntryKind.Images, DatePreset.Custom,
            new DateTime(2024, 1, 1), new DateTime(2024, 12, 31), SizePreset.Medium) { LastUsed = T0 });

        var reloaded = SearchHistory.Load(_path).Entries.Single();
        Assert.Equal(@"D:\Fotos", reloaded.Root);
        Assert.Equal(EntryKind.Images, reloaded.Kind);
        Assert.Equal(DatePreset.Custom, reloaded.Date);
        Assert.Equal(new DateTime(2024, 12, 31), reloaded.CustomTo);
        Assert.Equal(SizePreset.Medium, reloaded.Size);
        // El formato de las fechas depende de la configuracion regional: se comprueba el resto
        Assert.StartsWith(@"en D:\Fotos  ·  imágenes  ·  modificados del ", reloaded.Description);
        Assert.EndsWith("  ·  de 1 a 128 MB", reloaded.Description);
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
