using SqlObserver.Collector;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SqlObserver Collector");
builder.Services.AddHostedService<CollectorHostScaffold>();

IHost host = builder.Build();
await host.RunAsync();
