using EasySearcher.Core;
using EasySearcher.Core.Service;

namespace EasySearcher.Tests;

/// <summary>
/// App y servicio hablando por un named pipe real. Cada prueba usa un nombre de pipe propio para no
/// chocar con el servicio instalado ni con otras pruebas.
/// </summary>
public class PipeTests
{
    [Fact]
    public async Task Requests_and_responses_cross_the_pipe()
    {
        await WithServerAsync(new SearchEngine(TestVolume.BuildIndex()), client =>
        {
            Assert.Equal(10, client.GetStatus().Entries);

            var request = new SearchRequest("linkin", EntryKind.Audio, @"D:\Musica", false, SortColumn.Name, false,
                ModifiedFrom: new DateTime(2020, 1, 1), ModifiedTo: null);
            // Las fechas no existen en disco (volumen de prueba): el filtro de fecha deja 0
            Assert.Equal(0, client.Search(request).Total);

            var response = client.Search(request with { ModifiedFrom = null });
            Assert.Equal(1, response.Total);
            Assert.Equal(@"D:\Musica", response.Root);
            Assert.Equal("Linkin Park - Numb.mp3", client.GetRows(response.SearchId, 0, 10).Single().Name);
        });
    }

    [Fact]
    public async Task Service_errors_arrive_as_SearchServiceException()
    {
        await WithServerAsync(new SearchEngine(TestVolume.BuildIndex()), client =>
        {
            var error = Assert.Throws<SearchServiceException>(() => client.ResolveRoot(@"D:\NoExiste"));
            Assert.Contains("NoExiste", error.Message);
            // La conexion sigue sirviendo despues de un error
            Assert.True(client.GetStatus().Ready);
        });
    }

    [Fact]
    public void Connecting_without_a_server_fails_with_a_clear_message()
    {
        var error = Assert.Throws<SearchServiceException>(() => PipeSearchClient.Connect($"EasySearcher.Tests.{Guid.NewGuid():N}"));
        Assert.Contains("no esta disponible", error.Message);
    }

    static async Task WithServerAsync(ISearchService service, Action<PipeSearchClient> test)
    {
        var pipeName = $"EasySearcher.Tests.{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource();
        var server = PipeServer.RunAsync(service, pipeName, cts.Token);
        try
        {
            await Task.Run(() =>
            {
                using var client = PipeSearchClient.Connect(pipeName);
                test(client);
            });
        }
        finally
        {
            cts.Cancel();
            await server;
        }
    }
}
