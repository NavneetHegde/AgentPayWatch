using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Infrastructure.Cosmos;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace AgentPayWatch.Infrastructure.Tests;

[Collection("CosmosIntegration")]
public sealed class ProductMatchRepositoryTests : IAsyncLifetime
{
    private readonly CosmosFixture _fixture;
    private CosmosProductMatchRepository _repo = null!;

    public ProductMatchRepositoryTests(CosmosFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        Skip.If(!_fixture.IsAvailable,
            $"Cosmos DB emulator unreachable — start Aspire first. Reason: {_fixture.UnavailableReason}");
        _repo = new CosmosProductMatchRepository(_fixture.Client);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_fixture.IsAvailable)
            await _fixture.DeleteAllMatchDocumentsAsync();
    }

    // ── CreateAsync ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateAsync_StoresDocumentInMatchesContainer()
    {
        var watchId = Guid.NewGuid();
        var match = MakeMatch(watchId);

        await _repo.CreateAsync(match);

        // Read directly from Cosmos to verify persistence and field mapping.
        var response = await _fixture.MatchesContainer.ReadItemAsync<dynamic>(
            match.Id.ToString(), new PartitionKey(watchId.ToString()));
        dynamic doc = response.Resource;

        Assert.Equal(match.Id.ToString(), (string)doc.id);
        Assert.Equal(watchId.ToString(), (string)doc.watchRequestId);
        Assert.Equal("user-1", (string)doc.userId);
        Assert.Equal("Test Product", (string)doc.productName);
        Assert.Equal(99.99m, (decimal)doc.price);
        Assert.Equal("inStock", (string)doc.availability); // enum serialized as camelCase string
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetByIdAsync_ReturnsStoredMatch()
    {
        var watchId = Guid.NewGuid();
        var match = MakeMatch(watchId, price: 149.50m);
        await _repo.CreateAsync(match);

        var fetched = await _repo.GetByIdAsync(match.Id, watchId);

        Assert.NotNull(fetched);
        Assert.Equal(match.Id, fetched.Id);
        Assert.Equal(watchId, fetched.WatchRequestId);
        Assert.Equal("user-1", fetched.UserId);
        Assert.Equal("Test Product", fetched.ProductName);
        Assert.Equal(149.50m, fetched.Price);
        Assert.Equal("USD", fetched.Currency);
        Assert.Equal("BestSeller", fetched.Seller);
        Assert.Equal(ProductAvailability.InStock, fetched.Availability);
    }

    [SkippableFact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(result);
    }

    [SkippableFact]
    public async Task GetByIdAsync_ReturnsNull_WhenWatchRequestIdDoesNotMatchPartition()
    {
        var watchId = Guid.NewGuid();
        var match = MakeMatch(watchId);
        await _repo.CreateAsync(match);

        // Correct id, wrong partition key → point read misses.
        var result = await _repo.GetByIdAsync(match.Id, Guid.NewGuid());
        Assert.Null(result);
    }

    // ── GetByWatchRequestIdAsync ──────────────────────────────────────────────

    [SkippableFact]
    public async Task GetByWatchRequestIdAsync_ReturnsAllMatchesForWatch()
    {
        var watchId = Guid.NewGuid();
        var otherWatchId = Guid.NewGuid();

        await _repo.CreateAsync(MakeMatch(watchId, price: 10m));
        await _repo.CreateAsync(MakeMatch(watchId, price: 20m));
        await _repo.CreateAsync(MakeMatch(otherWatchId, price: 30m));

        var results = await _repo.GetByWatchRequestIdAsync(watchId);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(watchId, r.WatchRequestId));
    }

    [SkippableFact]
    public async Task GetByWatchRequestIdAsync_ReturnsEmpty_WhenNoMatchesExist()
    {
        var results = await _repo.GetByWatchRequestIdAsync(Guid.NewGuid());
        Assert.Empty(results);
    }

    [SkippableFact]
    public async Task GetByWatchRequestIdAsync_DoesNotReturnMatchesFromOtherWatches()
    {
        var watchA = Guid.NewGuid();
        var watchB = Guid.NewGuid();

        await _repo.CreateAsync(MakeMatch(watchA));
        await _repo.CreateAsync(MakeMatch(watchB));

        var resultsA = await _repo.GetByWatchRequestIdAsync(watchA);
        var resultsB = await _repo.GetByWatchRequestIdAsync(watchB);

        Assert.Single(resultsA);
        Assert.Single(resultsB);
        Assert.NotEqual(resultsA[0].Id, resultsB[0].Id);
    }

    // ── Field serialization ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateAsync_SerializesAllFields_IncludingDates()
    {
        var watchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var match = MakeMatch(watchId);
        match.MatchedAt = now;
        match.ExpiresAt = now.AddHours(24);
        match.Availability = ProductAvailability.LimitedStock;

        await _repo.CreateAsync(match);

        var fetched = await _repo.GetByIdAsync(match.Id, watchId);

        Assert.NotNull(fetched);
        // DateTimeOffset round-trip (millisecond precision).
        Assert.Equal(now.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond,
            fetched.MatchedAt.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond);
        Assert.Equal(ProductAvailability.LimitedStock, fetched.Availability);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProductMatch MakeMatch(Guid watchRequestId, decimal price = 99.99m) => new()
    {
        Id = Guid.NewGuid(),
        WatchRequestId = watchRequestId,
        UserId = "user-1",
        ProductName = "Test Product",
        Price = price,
        Currency = "USD",
        Seller = "BestSeller",
        ProductUrl = "https://store.example.com/test",
        MatchedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
        Availability = ProductAvailability.InStock
    };
}
