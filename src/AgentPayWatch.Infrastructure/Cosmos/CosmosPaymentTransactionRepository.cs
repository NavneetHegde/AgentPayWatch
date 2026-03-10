using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Azure.Cosmos;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosPaymentTransactionRepository : IPaymentTransactionRepository
{
    private readonly Container _container;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CosmosPaymentTransactionRepository(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("agentpaywatch", "transactions");
    }

    public async Task<PaymentTransaction> CreateAsync(PaymentTransaction transaction, CancellationToken ct = default)
    {
        using var stream = Serialize(transaction);
        using var response = await _container.CreateItemStreamAsync(
            stream,
            new PartitionKey(transaction.UserId),
            cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        return transaction;
    }

    public async Task<PaymentTransaction?> GetByIdAsync(Guid id, string userId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<JsonElement>(
                id.ToString(),
                new PartitionKey(userId),
                cancellationToken: ct);

            return JsonSerializer.Deserialize<PaymentTransaction>(response.Resource.GetRawText(), JsonOptions);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<PaymentTransaction>> GetByUserIdAsync(string userId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.userId = @userId")
            .WithParameter("@userId", userId);

        var iterator = _container.GetItemQueryIterator<JsonElement>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(userId)
            });

        var results = new List<PaymentTransaction>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                var transaction = JsonSerializer.Deserialize<PaymentTransaction>(item.GetRawText(), JsonOptions);
                if (transaction is not null)
                    results.Add(transaction);
            }
        }

        return results;
    }

    public async Task UpdateAsync(PaymentTransaction transaction, CancellationToken ct = default)
    {
        using var stream = Serialize(transaction);
        using var response = await _container.ReplaceItemStreamAsync(
            stream,
            transaction.Id.ToString(),
            new PartitionKey(transaction.UserId),
            cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    private static MemoryStream Serialize<T>(T entity)
    {
        var ms = new MemoryStream();
        JsonSerializer.Serialize(ms, entity, JsonOptions);
        ms.Position = 0;
        return ms;
    }
}
