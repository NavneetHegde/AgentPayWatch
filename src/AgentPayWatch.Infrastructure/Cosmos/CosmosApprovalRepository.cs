using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Azure.Cosmos;

namespace AgentPayWatch.Infrastructure.Cosmos;

public sealed class CosmosApprovalRepository : IApprovalRepository
{
    private readonly Container _container;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CosmosApprovalRepository(CosmosClient cosmosClient)
    {
        _container = cosmosClient.GetContainer("agentpaywatch", "approvals");
    }

    public async Task<ApprovalRecord> CreateAsync(ApprovalRecord approval, CancellationToken ct = default)
    {
        using var stream = Serialize(approval);
        using var response = await _container.CreateItemStreamAsync(
            stream,
            new PartitionKey(approval.WatchRequestId.ToString()),
            cancellationToken: ct);
        response.EnsureSuccessStatusCode();
        return approval;
    }

    public async Task<ApprovalRecord?> GetByIdAsync(Guid id, Guid watchRequestId, CancellationToken ct = default)
    {
        try
        {
            var response = await _container.ReadItemAsync<JsonElement>(
                id.ToString(),
                new PartitionKey(watchRequestId.ToString()),
                cancellationToken: ct);

            return JsonSerializer.Deserialize<ApprovalRecord>(response.Resource.GetRawText(), JsonOptions);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ApprovalRecord?> GetByTokenAsync(string token, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.approvalToken = @token")
            .WithParameter("@token", token);

        var iterator = _container.GetItemQueryIterator<JsonElement>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                return JsonSerializer.Deserialize<ApprovalRecord>(item.GetRawText(), JsonOptions);
            }
        }

        return null;
    }

    public async Task<ApprovalRecord?> GetByMatchIdAsync(Guid matchId, Guid watchRequestId, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.matchId = @matchId AND c.watchRequestId = @watchRequestId")
            .WithParameter("@matchId", matchId.ToString())
            .WithParameter("@watchRequestId", watchRequestId.ToString());

        var iterator = _container.GetItemQueryIterator<JsonElement>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(watchRequestId.ToString())
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                return JsonSerializer.Deserialize<ApprovalRecord>(item.GetRawText(), JsonOptions);
            }
        }

        return null;
    }

    public async Task UpdateAsync(ApprovalRecord approval, CancellationToken ct = default)
    {
        using var stream = Serialize(approval);
        using var response = await _container.ReplaceItemStreamAsync(
            stream,
            approval.Id.ToString(),
            new PartitionKey(approval.WatchRequestId.ToString()),
            cancellationToken: ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ApprovalRecord>> GetPendingExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var query = new QueryDefinition(
            "SELECT * FROM c WHERE c.decision = @decision AND c.expiresAt < @now")
            .WithParameter("@decision", "pending")
            .WithParameter("@now", now.ToString("o"));

        var iterator = _container.GetItemQueryIterator<JsonElement>(query);

        var results = new List<ApprovalRecord>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                var approval = JsonSerializer.Deserialize<ApprovalRecord>(item.GetRawText(), JsonOptions);
                if (approval is not null)
                    results.Add(approval);
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
