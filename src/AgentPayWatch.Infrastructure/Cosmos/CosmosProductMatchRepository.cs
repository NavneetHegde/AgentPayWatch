using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Azure.Cosmos;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosProductMatchRepository : IProductMatchRepository
{
    private readonly Container _container;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CosmosProductMatchRepository(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("agentpaywatch", "matches");
    }

    public async Task<ProductMatch> CreateAsync(ProductMatch match, CancellationToken ct = default)
    {
        using var stream = Serialize(match);
        using var response = await _container.CreateItemStreamAsync(
            stream,
            new PartitionKey(match.WatchRequestId.ToString()),
            cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        return match;
    }

    public async Task<ProductMatch?> GetByIdAsync(Guid id, Guid watchRequestId, CancellationToken ct = default)
    {
        using var response = await _container.ReadItemStreamAsync(
            id.ToString(),
            new PartitionKey(watchRequestId.ToString()),
            cancellationToken: ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<ProductMatch>(response.Content, JsonOptions, ct);
    }

    public async Task<IReadOnlyList<ProductMatch>> GetByWatchRequestIdAsync(Guid watchRequestId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.watchRequestId = @watchRequestId")
            .WithParameter("@watchRequestId", watchRequestId.ToString());

        var iterator = _container.GetItemQueryStreamIterator(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(watchRequestId.ToString())
            });

        return await ReadAllAsync<ProductMatch>(iterator, ct);
    }

    private static async Task<IReadOnlyList<T>> ReadAllAsync<T>(
        FeedIterator iterator, CancellationToken ct)
    {
        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            using var response = await iterator.ReadNextAsync(ct);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(response.Content, cancellationToken: ct);
            foreach (var item in doc.RootElement.GetProperty("Documents").EnumerateArray())
            {
                var entity = JsonSerializer.Deserialize<T>(item.GetRawText(), JsonOptions);
                if (entity is not null)
                    results.Add(entity);
            }
        }
        return results;
    }

    private static MemoryStream Serialize<T>(T entity)
    {
        var ms = new MemoryStream();
        JsonSerializer.Serialize(ms, entity, JsonOptions);
        ms.Position = 0;
        return ms;
    }
}
