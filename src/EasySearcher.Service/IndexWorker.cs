using EasySearcher.Core;
using EasySearcher.Core.Service;

namespace EasySearcher.Service;

sealed class IndexWorker(ILogger<IndexWorker> logger) : BackgroundService
{
    public const string ServiceName = "EasySearcher";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var drives = FileIndex.GetNtfsDrives();
        logger.LogInformation("Indexando {Drives}", string.Join(", ", drives.Select(d => d + ":")));

        using var engine = new SearchEngine(drives);

        // El pipe atiende desde ya: mientras se construye el indice, la app muestra "indexando"
        var server = PipeServer.RunAsync(engine, stoppingToken);

        await engine.Built;
        var status = engine.GetStatus();
        if (status.Ready)
            logger.LogInformation("Indice listo: {Entries:N0} entradas en {Seconds:N1} s", status.Entries, status.BuildSeconds);
        foreach (var failure in status.Failures)
            logger.LogWarning("No indexada: {Failure}", failure);

        var detailsClock = System.Diagnostics.Stopwatch.StartNew();
        int filled = await engine.DetailsLoaded;
        logger.LogInformation("Tamanos y fechas leidos de la $MFT: {Filled:N0} entradas en {Seconds:N1} s",
            filled, detailsClock.Elapsed.TotalSeconds);

        await server;
    }
}
