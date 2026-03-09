using AgentPayWatch.Agents.Payment;
using AgentPayWatch.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructureServices();

builder.Services.AddSingleton<PaymentProcessor>();
builder.Services.AddHostedService<PaymentWorker>();

var host = builder.Build();

host.Run();
