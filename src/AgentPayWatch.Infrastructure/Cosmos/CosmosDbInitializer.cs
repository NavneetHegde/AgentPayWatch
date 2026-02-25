using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosDbInitializer : IHostedService
{
    private readonly CosmosClient _cosmosClient;
    private readonly ILogger<CosmosDbInitializer> _logger;

    private const string DatabaseName = "agentpaywatch";

    private static readonly (string Name, string PartitionKeyPath)[] Containers =
    [
        ("watches", "/userId"),
        ("matches", "/watchRequestId"),
        ("approvals", "/watchRequestId"),
        ("transactions", "/userId")
    ];

    public CosmosDbInitializer(CosmosClient cosmosClient, ILogger<CosmosDbInitializer> logger)
    {
        _cosmosClient = cosmosClient;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 5;
        var delay = TimeSpan.FromSeconds(3);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Initializing Cosmos DB (attempt {Attempt}/{MaxRetries})...",
                    attempt, maxRetries);

                var databaseResponse = await _cosmosClient.CreateDatabaseIfNotExistsAsync(
                    DatabaseName, cancellationToken: cancellationToken);

                var database = databaseResponse.Database;

                foreach (var (name, partitionKeyPath) in Containers)
                {
                    await database.CreateContainerIfNotExistsAsync(
                        new ContainerProperties(name, partitionKeyPath),
                        cancellationToken: cancellationToken);

                    _logger.LogInformation(
                        "Ensured container '{ContainerName}' with partition key '{PartitionKey}'.",
                        name, partitionKeyPath);
                }

                _logger.LogInformation("Cosmos DB initialization completed successfully.");
                return;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex,
                    "Cosmos DB initialization attempt {Attempt} failed. Retrying in {Delay}s...",
                    attempt, delay.TotalSeconds);

                await Task.Delay(delay, cancellationToken);
                delay *= 2;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
