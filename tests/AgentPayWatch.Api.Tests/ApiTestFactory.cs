using AgentPayWatch.Domain.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace AgentPayWatch.Api.Tests;

public sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    public IWatchRequestRepository WatchRepository { get; } =
        Substitute.For<IWatchRequestRepository>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Provide a fake Cosmos connection string so the DI registration
        // of CosmosClient succeeds without a real emulator running.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Well-known public Cosmos emulator endpoint + key
                ["ConnectionStrings:cosmos"] =
                    "AccountEndpoint=https://localhost:8081/;AccountKey=" +
                    "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMcZcLu/d5CTRKlGkQ=="
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Remove any IHostedService registrations (e.g. CosmosDbInitializer)
            // so they don't attempt to connect to Cosmos on startup.
            var hostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();
            foreach (var d in hostedServices)
                services.Remove(d);

            // Replace the real Cosmos repository with our NSubstitute mock.
            services.RemoveAll<IWatchRequestRepository>();
            services.AddScoped<IWatchRequestRepository>(_ => WatchRepository);
        });

        builder.UseEnvironment("Testing");
    }
}
