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
        using var stream = Serialize(watchRequest);
        using var response = await _container.CreateItemStreamAsync(
            stream,
            new PartitionKey(watchRequest.UserId),
            cancellationToken: ct);

        response.EnsureSuccessStatusCode();
        watchRequest.ETag = response.Headers.ETag;
        return watchRequest;
    }

    public async Task<WatchRequest?> GetByIdAsync(Guid id, string? userId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId))
            return null;

        using var response = await _container.ReadItemStreamAsync(
            id.ToString(),
            new PartitionKey(userId),
            cancellationToken: ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var result = await JsonSerializer.DeserializeAsync<WatchRequest>(response.Content, JsonOptions, ct);
        if (result is not null)
            result.ETag = response.Headers.ETag;
        return result;
    }

    public async Task<IReadOnlyList<WatchRequest>> GetByUserIdAsync(string userId, CancellationToken ct = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.userId = @userId")
            .WithParameter("@userId", userId);

        return await ExecuteQueryAsync(query, new QueryRequestOptions { PartitionKey = new PartitionKey(userId) }, ct);
    }

    public async Task<IReadOnlyList<WatchRequest>> GetByStatusAsync(WatchStatus status, CancellationToken ct = default)
    {
        var statusString = JsonNamingPolicy.CamelCase.ConvertName(status.ToString());

        var query = new QueryDefinition("SELECT * FROM c WHERE c.status = @status")
            .WithParameter("@status", statusString);

        return await ExecuteQueryAsync(query, new QueryRequestOptions { MaxItemCount = 100 }, ct);
    }

    public async Task UpdateAsync(WatchRequest watchRequest, CancellationToken ct = default)
    {
        var options = new ItemRequestOptions();
        if (!string.IsNullOrEmpty(watchRequest.ETag))
            options.IfMatchEtag = watchRequest.ETag;

        using var stream = Serialize(watchRequest);
        using var response = await _container.ReplaceItemStreamAsync(
            stream,
            watchRequest.Id.ToString(),
            new PartitionKey(watchRequest.UserId),
            options,
            ct);

        response.EnsureSuccessStatusCode();
        watchRequest.ETag = response.Headers.ETag;
    }

    private async Task<IReadOnlyList<WatchRequest>> ExecuteQueryAsync(
        QueryDefinition query, QueryRequestOptions options, CancellationToken ct)
    {
        var iterator = _container.GetItemQueryStreamIterator(query, requestOptions: options);
        var results = new List<WatchRequest>();

        while (iterator.HasMoreResults)
        {
            using var response = await iterator.ReadNextAsync(ct);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(response.Content, cancellationToken: ct);
            foreach (var item in doc.RootElement.GetProperty("Documents").EnumerateArray())
            {
                var wr = JsonSerializer.Deserialize<WatchRequest>(item.GetRawText(), JsonOptions);
                if (wr is not null)
                    results.Add(wr);
            }
        }

        return results;
    }

    private static MemoryStream Serialize(WatchRequest watchRequest)
    {
        var ms = new MemoryStream();
        JsonSerializer.Serialize(ms, watchRequest, JsonOptions);
        ms.Position = 0;
        return ms;
    }
}
