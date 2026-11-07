using EasySearcher.App;
using EasySearcher.Core;
using EasySearcher.Core.Service;

namespace EasySearcher.Tests;

public class ListMessageTests
{
    static EngineStatus Status(bool ready = true, string[]? failures = null, bool detailsReady = true) =>
        new(ready, 100, 0, ["C"], [], failures ?? [], 2, Protocol.Version, 0, detailsReady);

    static SearchState Search(string text = "", string? root = null, EntryKind kind = EntryKind.All, SizeCriterion size = default) =>
        new(text, root, kind, DatePreset.Any, null, null, size);

    static readonly SizeCriterion Over100Mb = new(SizeMode.Larger, 100, SizeUnit.MB);

    [Fact]
    public void Without_connection_it_says_so_whatever_the_search()
    {
        var message = ListMessage.For(null, Search("linkin"), 5, null);
        Assert.StartsWith("Conectando", message!.Title);
    }

    [Fact]
    public void While_indexing_shows_progress_or_the_failure()
    {
        Assert.StartsWith("Indexando", ListMessage.For(Status(ready: false), Search(), null, null)!.Title);

        var failed = ListMessage.For(Status(ready: false, failures: ["C: sin permisos"]), Search(), null, null);
        Assert.Equal("C: sin permisos", failed!.Detail);
    }

    [Fact]
    public void With_nothing_typed_it_invites_to_search_and_offers_the_recent_ones()
    {
        var message = ListMessage.For(Status(), Search(), null, null);
        Assert.Equal("Escribe para buscar", message!.Title);
        Assert.True(message.ShowRecent);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(null)]   // la primera respuesta aun no llega: no hay que parpadear
    public void With_results_or_a_search_on_its_way_there_is_no_message(int? total)
    {
        Assert.Null(ListMessage.For(Status(), Search("linkin"), total, null));
    }

    [Fact]
    public void No_results_explains_the_context_and_offers_only_what_applies()
    {
        var both = ListMessage.For(Status(), Search("linkin", @"D:\Neyder", size: Over100Mb), 0, null)!;
        Assert.Equal("Sin resultados para «linkin»", both.Title);
        Assert.Equal(@"en D:\Neyder  ·  de más de 100 MB", both.Detail);
        Assert.True(both.OfferClearFilters);
        Assert.True(both.OfferAllDrives);

        var neither = ListMessage.For(Status(), Search("linkin"), 0, null)!;
        Assert.False(neither.OfferClearFilters);
        Assert.False(neither.OfferAllDrives);
    }

    [Fact]
    public void A_kind_filter_alone_can_be_cleared()
    {
        Assert.True(ListMessage.For(Status(), Search("linkin", kind: EntryKind.Videos), 0, null)!.OfferClearFilters);
    }

    [Fact]
    public void No_results_warns_when_sizes_are_still_being_read()
    {
        var reading = ListMessage.For(Status(detailsReady: false), Search(size: Over100Mb), 0, null)!;
        Assert.Contains("pueden aparecer más", reading.Detail);

        // El texto no depende de los tamanos: el aviso seria falso
        var byName = ListMessage.For(Status(detailsReady: false), Search("linkin"), 0, null)!;
        Assert.DoesNotContain("pueden aparecer", byName.Detail);
    }

    [Fact]
    public void An_error_beats_the_last_results()
    {
        var message = ListMessage.For(Status(), Search("x", @"D:\NoExiste"), 5, "La carpeta no existe")!;
        Assert.Equal("La carpeta no existe", message.Detail);
        Assert.True(message.OfferAllDrives);
    }

    [Fact]
    public void A_bad_folder_can_be_left_even_when_searching_everywhere()
    {
        var message = ListMessage.For(Status(), Search("x"), null, "No existe la carpeta", rootFailed: true)!;
        Assert.True(message.OfferAllDrives);
    }
}
