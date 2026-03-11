using System.Text.Json;
using System.Text.Json.Serialization;
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
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public IWatchRequestRepository WatchRepository { get; } =
        Substitute.For<IWatchRequestRepository>();

    public IApprovalRepository ApprovalRepository { get; } =
        Substitute.For<IApprovalRepository>();

    public IProductMatchRepository MatchRepository { get; } =
        Substitute.For<IProductMatchRepository>();

    public IPaymentTransactionRepository TransactionRepository { get; } =
        Substitute.For<IPaymentTransactionRepository>();

    public IEventPublisher EventPublisher { get; } =
        Substitute.For<IEventPublisher>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Provide fake connection strings so DI registrations succeed without
        // real emulators running.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:cosmos"] =
                    "AccountEndpoint=https://localhost:8081/;AccountKey=" +
                    "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMcZcLu/d5CTRKlGkQ==",
                ["ConnectionStrings:messaging"] =
                    "Endpoint=sb://localhost/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=fake"
            });
        });

        builder.ConfigureTestServices(services =>
        {
            // Remove any IHostedService registrations (e.g. CosmosDbInitializer)
            // so they don't attempt to connect to external services on startup.
            var hostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();
            foreach (var d in hostedServices)
                services.Remove(d);

            // Replace real infrastructure with NSubstitute mocks.
            services.RemoveAll<IWatchRequestRepository>();
            services.AddScoped<IWatchRequestRepository>(_ => WatchRepository);

            services.RemoveAll<IApprovalRepository>();
            services.AddScoped<IApprovalRepository>(_ => ApprovalRepository);

            services.RemoveAll<IProductMatchRepository>();
            services.AddScoped<IProductMatchRepository>(_ => MatchRepository);

            services.RemoveAll<IPaymentTransactionRepository>();
            services.AddScoped<IPaymentTransactionRepository>(_ => TransactionRepository);

            services.RemoveAll<IEventPublisher>();
            services.AddSingleton<IEventPublisher>(_ => EventPublisher);
        });

        builder.UseEnvironment("Testing");
    }
}
