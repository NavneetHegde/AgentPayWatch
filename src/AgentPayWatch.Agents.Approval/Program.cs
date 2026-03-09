using AgentPayWatch.Agents.Approval;
using AgentPayWatch.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructureServices();

builder.Services.AddHostedService<ApprovalWorker>();
builder.Services.AddHostedService<ApprovalTimeoutWorker>();

var host = builder.Build();
await host.RunAsync();
