using LibertyRoute.Service;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "LibertyRoute Network Service";
});

builder.Services.AddLibertyRouteCoreServices();

await builder.Build().RunAsync();