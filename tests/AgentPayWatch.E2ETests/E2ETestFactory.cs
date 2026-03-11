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

namespace AgentPayWatch.E2ETests;

/// <summary>
/// WebApplicationFactory that wires the API with shared in-memory repositories,
/// so agent simulators operating on the same stores can interact with the same state.
/// </summary>
public sealed class E2ETestFactory : WebApplicationFactory<Program>
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Shared in-memory stores — used by both the API and the AgentSimulator.
    public InMemoryWatchRequestRepository WatchRepo { get; } = new();
    public InMemoryProductMatchRepository MatchRepo { get; } = new();
    public InMemoryApprovalRepository ApprovalRepo { get; } = new();
    public InMemoryPaymentTransactionRepository TransactionRepo { get; } = new();
    public InMemoryEventPublisher EventPublisher { get; } = new();
    public ConfigurableProductSource ProductSource { get; } = new();
    public FakeA2PClient A2PClient { get; } = new();
    public ConfigurablePaymentProvider PaymentProvider { get; } = new();

    /// <summary>Creates an AgentSimulator backed by this factory's shared stores.</summary>
    public AgentSimulator CreateAgentSimulator() =>
        new(WatchRepo, MatchRepo, ApprovalRepo, TransactionRepo,
            EventPublisher, ProductSource, A2PClient, PaymentProvider);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
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
            // Remove hosted services so they don't attempt real connections on startup.
            var hostedServices = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();
            foreach (var d in hostedServices)
                services.Remove(d);

            // Replace all repositories and cross-cutting services with shared in-memory fakes.
            services.RemoveAll<IWatchRequestRepository>();
            services.AddScoped<IWatchRequestRepository>(_ => WatchRepo);

            services.RemoveAll<IProductMatchRepository>();
            services.AddScoped<IProductMatchRepository>(_ => MatchRepo);

            services.RemoveAll<IApprovalRepository>();
            services.AddScoped<IApprovalRepository>(_ => ApprovalRepo);

            services.RemoveAll<IPaymentTransactionRepository>();
            services.AddScoped<IPaymentTransactionRepository>(_ => TransactionRepo);

            services.RemoveAll<IEventPublisher>();
            services.AddSingleton<IEventPublisher>(_ => EventPublisher);

            services.RemoveAll<IProductSource>();
            services.AddSingleton<IProductSource>(_ => ProductSource);

            services.RemoveAll<IA2PClient>();
            services.AddSingleton<IA2PClient>(_ => A2PClient);

            services.RemoveAll<IPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(_ => PaymentProvider);
        });

        builder.UseEnvironment("Testing");
    }
}
