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
        var root = ToDocument(match);
        root["id"] = match.Id.ToString();
        root["watchRequestId"] = match.WatchRequestId.ToString();

        await _container.CreateItemAsync(
            root,
            new PartitionKey(match.WatchRequestId.ToString()),
            cancellationToken: ct);

        return match;
    }

    public async Task<ProductMatch?> GetByIdAsync(Guid id, Guid watchRequestId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<JsonElement>(
                id.ToString(),
                new PartitionKey(watchRequestId.ToString()),
                cancellationToken: ct);

            return JsonSerializer.Deserialize<ProductMatch>(response.Resource.GetRawText(), JsonOptions);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProductMatch>> GetByWatchRequestIdAsync(Guid watchRequestId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.watchRequestId = @watchRequestId")
            .WithParameter("@watchRequestId", watchRequestId.ToString());

        var iterator = _container.GetItemQueryIterator<JsonElement>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(watchRequestId.ToString())
            });

        var results = new List<ProductMatch>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                var match = JsonSerializer.Deserialize<ProductMatch>(item.GetRawText(), JsonOptions);
                if (match is not null)
                    results.Add(match);
            }
        }

        return results;
    }

    private static Dictionary<string, object> ToDocument<T>(T entity)
    {
        var json = JsonSerializer.Serialize(entity, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        var root = new Dictionary<string, object>();
        foreach (var property in doc.RootElement.EnumerateObject())
            root[property.Name] = property.Value.Clone();
        return root;
    }
}
