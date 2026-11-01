using EasySearcher.Service;

// Servicio de Windows que mantiene el indice en memoria y atiende a la app por un named pipe.
// Ejecutado directamente (como administrador) funciona como aplicacion de consola, util para depurar.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = IndexWorker.ServiceName);
builder.Services.AddHostedService<IndexWorker>();
builder.Build().Run();
