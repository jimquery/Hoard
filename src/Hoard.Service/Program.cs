using Hoard.Core;
using Hoard.Core.Configuration;
using Hoard.Service;

// Runs as a console app for development (`dotnet run`) and as a Windows Service once installed with
// `sc.exe create Hoard binPath= "...\HoardService.exe"` — see README.md. Both share the same hoard.json as the desktop app.
var settings = SettingsStore.Load();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "Hoard");
builder.Logging.AddEventLog(o => o.SourceName = "Hoard");
builder.Services.AddHoardCore(settings);
builder.Services.AddHostedService<SyncWorker>();

var host = builder.Build();
host.Run();
