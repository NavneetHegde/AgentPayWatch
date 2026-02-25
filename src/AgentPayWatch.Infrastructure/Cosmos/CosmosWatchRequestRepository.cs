using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Azure.Cosmos;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosWatchRequestRepository : IWatchRequestRepository
{
    private readonly Container _container;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CosmosWatchRequestRepository(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("agentpaywatch", "watches");
    }

    public async Task<WatchRequest> CreateAsync(WatchRequest watchRequest, CancellationToken ct = default)
    {
        var root = ToDocument(watchRequest);
        root["id"] = watchRequest.Id.ToString();
        root["userId"] = watchRequest.UserId;

        var response = await _container.CreateItemAsync(
            root,
            new PartitionKey(watchRequest.UserId),
            cancellationToken: ct);

        watchRequest.ETag = response.ETag;
        return watchRequest;
    }

    public async Task<WatchRequest?> GetByIdAsync(Guid id, string? userId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            return null;

        try
        {
            var response = await _container.ReadItemAsync<JsonElement>(
                id.ToString(),
                new PartitionKey(userId),
                cancellationToken: ct);

            var watchRequest = JsonSerializer.Deserialize<WatchRequest>(
                response.Resource.GetRawText(), JsonOptions);

            if (watchRequest is not null)
                watchRequest.ETag = response.ETag;

            return watchRequest;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WatchRequest>> GetByUserIdAsync(string userId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.userId = @userId")
            .WithParameter("@userId", userId);

        var iterator = _container.GetItemQueryIterator<JsonElement>(
            query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) });

        var results = new List<WatchRequest>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                var watchRequest = JsonSerializer.Deserialize<WatchRequest>(item.GetRawText(), JsonOptions);
                if (watchRequest is not null)
                    results.Add(watchRequest);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<WatchRequest>> GetByStatusAsync(WatchStatus status, CancellationToken ct = default)
    {
        var statusString = status.ToString().ToLowerInvariant();

        var query = new QueryDefinition("SELECT * FROM c WHERE c.status = @status")
            .WithParameter("@status", statusString);

        var iterator = _container.GetItemQueryIterator<JsonElement>(
            query,
            requestOptions: new QueryRequestOptions { MaxItemCount = 100 });

        var results = new List<WatchRequest>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                var watchRequest = JsonSerializer.Deserialize<WatchRequest>(item.GetRawText(), JsonOptions);
                if (watchRequest is not null)
                    results.Add(watchRequest);
            }
        }

        return results;
    }

    public async Task UpdateAsync(WatchRequest watchRequest, CancellationToken ct = default)
    {
        var root = ToDocument(watchRequest);
        root["id"] = watchRequest.Id.ToString();
        root["userId"] = watchRequest.UserId;

        var options = new ItemRequestOptions();
        if (!string.IsNullOrEmpty(watchRequest.ETag))
            options.IfMatchEtag = watchRequest.ETag;

        var response = await _container.ReplaceItemAsync(
            root,
            watchRequest.Id.ToString(),
            new PartitionKey(watchRequest.UserId),
            options,
            ct);

        watchRequest.ETag = response.ETag;
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
