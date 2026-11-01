using EasySearcher.Core;
using EasySearcher.Core.Service;

namespace EasySearcher.Tests;

public class SearchEngineTests
{
    static SearchEngine Engine() => new(TestVolume.BuildIndex());

    static SearchRequest Request(string text, string? root = null, int previous = 0) =>
        new(text, EntryKind.All, root, false, SortColumn.Name, false, previous);

    [Fact]
    public void Status_reports_entries_and_protocol()
    {
        var status = Engine().GetStatus();
        Assert.True(status.Ready);
        Assert.Equal(10, status.Entries);
        Assert.Equal(["D"], status.Drives);
        Assert.Equal(Protocol.Version, status.ProtocolVersion);
    }

    [Fact]
    public void Search_results_are_served_by_pages()
    {
        var engine = Engine();
        var response = engine.Search(Request("linkin"));
        Assert.Equal(2, response.Total);

        var rows = engine.GetRows(response.SearchId, 0, 10);
        Assert.Equal(["Linkin Park - Live.mp4", "Linkin Park - Numb.mp3"], rows.Select(r => r.Name));
        Assert.Equal(@"D:\Musica\Live", rows[0].Directory);
        Assert.Single(engine.GetRows(response.SearchId, 1, 10));
        Assert.Empty(engine.GetRows(response.SearchId, 5, 10));
    }

    [Fact]
    public void Repeating_an_identical_search_reports_unchanged()
    {
        var engine = Engine();
        var first = engine.Search(Request("linkin"));
        var again = engine.Search(Request("linkin", previous: first.SearchId));
        Assert.True(again.Unchanged);
        Assert.Equal(first.SearchId, again.SearchId);

        var different = engine.Search(Request("numb", previous: first.SearchId));
        Assert.False(different.Unchanged);
    }

    [Fact]
    public void Old_searches_are_evicted_and_return_no_rows()
    {
        var engine = Engine();
        int first = engine.Search(Request("linkin")).SearchId;
        for (int i = 0; i < 10; i++) engine.Search(Request("a"));
        Assert.Empty(engine.GetRows(first, 0, 10));
    }

    [Fact]
    public void Invalid_root_is_reported_as_a_user_error()
    {
        var engine = Engine();
        var error = Assert.Throws<SearchServiceException>(() => engine.Search(Request("x", root: @"D:\NoExiste")));
        Assert.Contains("NoExiste", error.Message);
        Assert.Equal(@"D:\Musica", engine.ResolveRoot(@"d:\MUSICA"));
    }
}
