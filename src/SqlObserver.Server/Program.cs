using SqlObserver.Server;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "SqlObserver Server");
WebApplication app = builder.Build();

app.MapSqlObserverScaffoldEndpoints();

await app.RunAsync();
