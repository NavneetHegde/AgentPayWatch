using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Models;
using Xunit;

namespace AgentPayWatch.Agents.ProductWatch.Tests;

public sealed class MatchingServiceTests
{
    // ── IsMatch: price checks ─────────────────────────────────────────────────

    [Fact]
    public void IsMatch_ReturnsFalse_WhenPriceAboveMaxPrice()
    {
        var watch = MakeWatch(maxPrice: 100m);
        var listing = MakeListing(price: 100.01m);

        Assert.False(MatchingService.IsMatch(watch, listing));
    }

    [Fact]
    public void IsMatch_ReturnsTrue_WhenPriceEqualsMaxPrice()
    {
        var watch = MakeWatch(maxPrice: 100m);
        var listing = MakeListing(price: 100m);

        Assert.True(MatchingService.IsMatch(watch, listing));
    }

    [Fact]
    public void IsMatch_ReturnsTrue_WhenPriceBelowMaxPrice()
    {
        var watch = MakeWatch(maxPrice: 200m);
        var listing = MakeListing(price: 150m);

        Assert.True(MatchingService.IsMatch(watch, listing));
    }

    // ── IsMatch: preferred sellers ────────────────────────────────────────────

    [Fact]
    public void IsMatch_ReturnsTrue_WhenNoPreferredSellers()
    {
        var watch = MakeWatch(maxPrice: 500m, preferredSellers: []);
        var listing = MakeListing(price: 400m, seller: "AnySeller");

        Assert.True(MatchingService.IsMatch(watch, listing));
    }

    [Fact]
    public void IsMatch_ReturnsTrue_WhenPreferredSellersIsNull()
    {
        var watch = MakeWatch(maxPrice: 500m);
        watch.PreferredSellers = null!;
        var listing = MakeListing(price: 400m, seller: "AnySeller");

        Assert.True(MatchingService.IsMatch(watch, listing));
    }

    [Fact]
    public void IsMatch_ReturnsTrue_WhenSellerInPreferredList()
    {
        var watch = MakeWatch(maxPrice: 500m, preferredSellers: ["TechZone", "GameVault"]);
        var listing = MakeListing(price: 400m, seller: "TechZone");

        Assert.True(MatchingService.IsMatch(watch, listing));
    }

    [Fact]
    public void IsMatch_ReturnsFalse_WhenSellerNotInPreferredList()
    {
        var watch = MakeWatch(maxPrice: 500m, preferredSellers: ["TechZone"]);
        var listing = MakeListing(price: 400m, seller: "UnknownSeller");

        Assert.False(MatchingService.IsMatch(watch, listing));
    }

    [Fact]
    public void IsMatch_SellerComparison_IsCaseInsensitive()
    {
        var watch = MakeWatch(maxPrice: 500m, preferredSellers: ["techzone"]);
        var listing = MakeListing(price: 400m, seller: "TechZone");

        Assert.True(MatchingService.IsMatch(watch, listing));
    }

    [Fact]
    public void IsMatch_ReturnsFalse_WhenPriceExceedsMaxEvenWithMatchingSeller()
    {
        var watch = MakeWatch(maxPrice: 100m, preferredSellers: ["TechZone"]);
        var listing = MakeListing(price: 200m, seller: "TechZone");

        Assert.False(MatchingService.IsMatch(watch, listing));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static WatchRequest MakeWatch(decimal maxPrice, string[]? preferredSellers = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            ProductName = "Test Product",
            MaxPrice = maxPrice,
            PreferredSellers = preferredSellers ?? []
        };

    private static ProductListing MakeListing(decimal price, string seller = "TestSeller") =>
        new(
            Name: "Test Product",
            Price: price,
            Currency: "USD",
            Seller: seller,
            Url: "https://store.example.com/test",
            Availability: ProductAvailability.InStock);
}
