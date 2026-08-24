using SqlObserver.Collector;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SqlObserver Collector");
builder.Services.AddSqlObserverCollectorRuntime(builder.Configuration);

IHost host = builder.Build();
await host.RunAsync();
