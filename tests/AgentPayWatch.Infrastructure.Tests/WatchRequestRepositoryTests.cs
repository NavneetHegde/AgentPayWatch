using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Infrastructure.Cosmos;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace AgentPayWatch.Infrastructure.Tests;

[Collection("CosmosIntegration")]
public sealed class WatchRequestRepositoryTests : IAsyncLifetime
{
    private readonly CosmosFixture _fixture;
    private CosmosWatchRequestRepository _repo = null!;

    public WatchRequestRepositoryTests(CosmosFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        Skip.If(!_fixture.IsAvailable,
            $"Cosmos DB emulator unreachable — start Aspire first ('aspire run .\\appHost\\apphost.cs'), " +
            $"then run tests via run-infra-tests.sh. Reason: {_fixture.UnavailableReason}");
        _repo = new CosmosWatchRequestRepository(_fixture.Client);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_fixture.IsAvailable)
            await _fixture.DeleteAllDocumentsAsync();
    }

    // ── Test 1 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateAsync_StoresDocumentInCosmos()
    {
        var entity = MakeWatch("user-1");

        var created = await _repo.CreateAsync(entity);

        // ETag must be set by Cosmos after write
        Assert.NotEmpty(created.ETag);

        // Read directly from Cosmos to confirm persistence
        var response = await _fixture.Container.ReadItemAsync<dynamic>(
            created.Id.ToString(), new PartitionKey("user-1"));
        dynamic doc = response.Resource;

        Assert.Equal(created.Id.ToString(), (string)doc.id);
        Assert.Equal("user-1", (string)doc.userId);
        Assert.Equal("active", (string)doc.status);
        Assert.Equal("Widget Pro", (string)doc.productName);
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetByIdAsync_ReturnsStoredDocument()
    {
        var entity = MakeWatch("user-2", maxPrice: 49.99m);
        await _repo.CreateAsync(entity);

        var fetched = await _repo.GetByIdAsync(entity.Id, "user-2");

        Assert.NotNull(fetched);
        Assert.Equal(entity.Id, fetched.Id);
        Assert.Equal("user-2", fetched.UserId);
        Assert.Equal(WatchStatus.Active, fetched.Status);
        Assert.Equal("Widget Pro", fetched.ProductName);
        Assert.Equal(49.99m, fetched.MaxPrice);
        Assert.Empty(fetched.StatusHistory);
        Assert.NotEmpty(fetched.ETag);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task UpdateAsync_PersistsStatusChange()
    {
        var entity = MakeWatch("user-3");
        await _repo.CreateAsync(entity);
        var originalETag = entity.ETag;

        entity.UpdateStatus(WatchStatus.Paused, "user requested pause");
        await _repo.UpdateAsync(entity);

        var fetched = await _repo.GetByIdAsync(entity.Id, "user-3");

        Assert.NotNull(fetched);
        Assert.Equal(WatchStatus.Paused, fetched.Status);
        Assert.Single(fetched.StatusHistory);
        Assert.Equal(WatchStatus.Active, fetched.StatusHistory[0].From);
        Assert.Equal(WatchStatus.Paused, fetched.StatusHistory[0].To);
        Assert.Equal("user requested pause", fetched.StatusHistory[0].Reason);
        // ETag must have changed after the update
        Assert.NotEqual(originalETag, fetched.ETag);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CancelUpdate_SoftDeletesDocument()
    {
        var entity = MakeWatch("user-4");
        await _repo.CreateAsync(entity);

        entity.UpdateStatus(WatchStatus.Cancelled, "user cancelled");
        await _repo.UpdateAsync(entity);

        // Document must still exist in Cosmos (soft delete, not physical delete)
        var fetched = await _repo.GetByIdAsync(entity.Id, "user-4");

        Assert.NotNull(fetched);
        Assert.Equal(WatchStatus.Cancelled, fetched.Status);
        Assert.Single(fetched.StatusHistory);
        Assert.Equal(WatchStatus.Active, fetched.StatusHistory[0].From);
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetByUserIdAsync_ReturnsOnlyMatchingUserId()
    {
        await _repo.CreateAsync(MakeWatch("user-a"));
        await _repo.CreateAsync(MakeWatch("user-a"));
        await _repo.CreateAsync(MakeWatch("user-b"));

        var results = await _repo.GetByUserIdAsync("user-a");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("user-a", r.UserId));
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task UpdateAsync_ETagConcurrency_ThrowsOnConflict()
    {
        var entity = MakeWatch("user-6");
        await _repo.CreateAsync(entity);

        // Two independent reads → same ETag
        var copy1 = await _repo.GetByIdAsync(entity.Id, "user-6");
        var copy2 = await _repo.GetByIdAsync(entity.Id, "user-6");

        Assert.NotNull(copy1);
        Assert.NotNull(copy2);

        // First update succeeds, advancing the ETag in Cosmos
        copy1.UpdateStatus(WatchStatus.Paused, "first updater");
        await _repo.UpdateAsync(copy1);

        // Second update carries a stale ETag → must get 412 PreconditionFailed
        copy2.UpdateStatus(WatchStatus.Paused, "second updater");
        var ex = await Assert.ThrowsAsync<CosmosException>(() => _repo.UpdateAsync(copy2));
        Assert.Equal(System.Net.HttpStatusCode.PreconditionFailed, ex.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WatchRequest MakeWatch(string userId, decimal maxPrice = 99.99m) => new()
    {
        UserId = userId,
        ProductName = "Widget Pro",
        MaxPrice = maxPrice,
        Currency = "USD",
        PaymentMethodToken = "tok_test",
        PhoneNumber = "+15550001234"
    };
}

[CollectionDefinition("CosmosIntegration")]
public sealed class CosmosIntegrationCollection : ICollectionFixture<CosmosFixture> { }
