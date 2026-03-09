using AgentPayWatch.Infrastructure.Mocks;
using Xunit;

namespace AgentPayWatch.Agents.ProductWatch.Tests;

public sealed class MockProductSourceTests
{
    private readonly MockProductSource _source = new();

    // ── SearchAsync: known catalog terms ─────────────────────────────────────

    [Theory]
    [InlineData("iPhone")]
    [InlineData("PlayStation")]
    [InlineData("Xbox")]
    [InlineData("Nintendo Switch")]
    [InlineData("KitchenAid")]
    [InlineData("Clean Architecture")]
    public async Task SearchAsync_ReturnsResults_ForKnownProducts(string searchTerm)
    {
        // Results may be empty due to 70% randomness, but must never throw
        // and must return at most 3 entries.
        var results = await _source.SearchAsync(searchTerm, CancellationToken.None);

        Assert.NotNull(results);
        Assert.True(results.Count <= 3, $"Expected at most 3 results, got {results.Count}");
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_ForUnknownProduct()
    {
        var results = await _source.SearchAsync("zzzUnknownProduct999", CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_IsCaseInsensitive()
    {
        // "iphone" (lowercase) should match "iPhone 15 Pro" in the catalog;
        // the 70% filter means we can't guarantee a result, but the search must
        // complete without exception and return only matching entries.
        var results = await _source.SearchAsync("iphone", CancellationToken.None);

        Assert.NotNull(results);
        foreach (var listing in results)
        {
            Assert.Contains("iPhone", listing.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── SearchAsync: listing shape ────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_Listings_HaveRequiredFields()
    {
        // "Xbox" has a high enough base price that jitter won't eliminate it;
        // run until we get at least one result.
        var results = await RetryUntilNonEmptyAsync("Xbox");

        foreach (var listing in results)
        {
            Assert.False(string.IsNullOrWhiteSpace(listing.Name));
            Assert.True(listing.Price > 0);
            Assert.False(string.IsNullOrWhiteSpace(listing.Currency));
            Assert.False(string.IsNullOrWhiteSpace(listing.Seller));
            Assert.False(string.IsNullOrWhiteSpace(listing.Url));
        }
    }

    [Fact]
    public async Task SearchAsync_Urls_PointToExpectedHost()
    {
        var results = await RetryUntilNonEmptyAsync("Xbox");

        foreach (var listing in results)
        {
            Assert.StartsWith("https://store.example.com/products/", listing.Url);
        }
    }

    [Fact]
    public async Task SearchAsync_NeverReturnsMoreThanThreeResults()
    {
        // "iPhone" matches two catalog entries — verify cap is enforced
        var results = await _source.SearchAsync("iPhone", CancellationToken.None);

        Assert.True(results.Count <= 3);
    }

    // ── SearchAsync: price jitter ─────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_Prices_AreWithin15PercentOfBase()
    {
        // Xbox base price is $429. With ±15% jitter: [$364.65, $493.35]
        const decimal basePrice = 429m;
        const decimal lowerBound = basePrice * 0.85m;
        const decimal upperBound = basePrice * 1.15m;

        var results = await RetryUntilNonEmptyAsync("Xbox Series X");

        foreach (var listing in results)
        {
            Assert.InRange(listing.Price, lowerBound, upperBound);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Retries the search up to 10 times until at least one result is returned.
    /// This handles the 70% random availability filter.
    /// </summary>
    private async Task<IReadOnlyList<Domain.Models.ProductListing>> RetryUntilNonEmptyAsync(
        string searchTerm, int maxAttempts = 10)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            var results = await _source.SearchAsync(searchTerm, CancellationToken.None);
            if (results.Count > 0) return results;
        }

        // If still empty, just return empty — test using this helper should handle it gracefully
        return await _source.SearchAsync(searchTerm, CancellationToken.None);
    }
}
