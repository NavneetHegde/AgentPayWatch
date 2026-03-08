using AgentPayWatch.Api.Endpoints;
using AgentPayWatch.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructureServices();

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, context, ct) =>
    {
        document.Info = new()
        {
            Title = "AgentPay Watch API",
            Version = "v1",
            Description = "Autonomous agent-driven payment platform. Agents watch, humans approve, payments happen."
        };
        return Task.CompletedTask;
    });
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "AgentPay Watch";
        options.Theme = ScalarTheme.Moon;
        options.DefaultHttpClient = new(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });
}

app.MapDefaultEndpoints();
app.MapWatchEndpoints();
app.MapDebugEndpoints(); // TEMPORARY: Remove in Phase 4

app.Run();

// Expose Program to the test project
public partial class Program { }
