using LibertyRoute.Engine;
using LibertyRoute.Networking;
using LibertyRoute.Recovery;
using LibertyRoute.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LibertyRoute Network Service";
});

builder.Services.AddSingleton<INetworkStateManager, WindowsNetworkStateManager>();
builder.Services.AddSingleton<ITransactionJournal, FileTransactionJournal>();
builder.Services.AddSingleton<RecoveryManager>();
builder.Services.AddSingleton<IConnectionEngine, WireGuardEngine>();
builder.Services.AddSingleton<ConnectionController>();
builder.Services.AddHostedService<LibertyRouteWorker>();

await builder.Build().RunAsync();
