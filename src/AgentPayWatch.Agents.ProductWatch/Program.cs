using AgentPayWatch.Agents.ProductWatch;
using AgentPayWatch.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructureServices();

builder.Services.AddHostedService<ProductWatchWorker>();

var host = builder.Build();
await host.RunAsync();
