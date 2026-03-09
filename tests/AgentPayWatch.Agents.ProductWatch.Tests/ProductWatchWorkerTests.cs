using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Domain.Models;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPayWatch.Agents.ProductWatch.Tests;

public sealed class ProductWatchWorkerTests
{
    // ── Test 1: No active watches → nothing published ─────────────────────────

    [Fact]
    public async Task ScanAsync_DoesNothing_WhenNoActiveWatches()
    {
        var (matchRepo, publisher) = await RunOneScanAsync(
            watches: [],
            listings: [MakeListing(400m)]);

        Assert.Empty(matchRepo.CreatedMatches);
        Assert.Empty(publisher.PublishedEvents);
    }

    // ── Test 2: Active watch with no product listings → no match ─────────────

    [Fact]
    public async Task ScanAsync_DoesNotMatch_WhenNoListingsFound()
    {
        var watch = MakeActiveWatch(maxPrice: 500m);
        var (matchRepo, publisher) = await RunOneScanAsync(
            watches: [watch],
            listings: []);

        Assert.Empty(matchRepo.CreatedMatches);
        Assert.Empty(publisher.PublishedEvents);
    }

    // ── Test 3: Listing above max price → no match ────────────────────────────

    [Fact]
    public async Task ScanAsync_DoesNotMatch_WhenAllListingsAboveMaxPrice()
    {
        var watch = MakeActiveWatch(maxPrice: 100m);
        var (matchRepo, publisher) = await RunOneScanAsync(
            watches: [watch],
            listings: [MakeListing(price: 150m)]);

        Assert.Empty(matchRepo.CreatedMatches);
        Assert.Empty(publisher.PublishedEvents);
    }

    // ── Test 4: Matching listing → ProductMatch persisted ────────────────────

    [Fact]
    public async Task ScanAsync_CreatesProductMatch_WhenListingMeetsCriteria()
    {
        var watch = MakeActiveWatch(maxPrice: 500m);
        var listing = MakeListing(price: 400m, seller: "TechZone");
        var watchRepo = new FakeWatchRequestRepository(watch);
        var (matchRepo, _) = await RunOneScanAsync(watchRepo, [listing]);

        Assert.Single(matchRepo.CreatedMatches);
        var match = matchRepo.CreatedMatches[0];
        Assert.Equal(watch.Id, match.WatchRequestId);
        Assert.Equal(watch.UserId, match.UserId);
        Assert.Equal(listing.Name, match.ProductName);
        Assert.Equal(listing.Price, match.Price);
        Assert.Equal(listing.Seller, match.Seller);
    }

    // ── Test 5: Match found → watch status updated to Matched ────────────────

    [Fact]
    public async Task ScanAsync_UpdatesWatchStatus_ToMatched_WhenMatchFound()
    {
        var watch = MakeActiveWatch(maxPrice: 500m);
        var watchRepo = new FakeWatchRequestRepository(watch);
        await RunOneScanAsync(watchRepo, [MakeListing(400m)]);

        Assert.Equal(WatchStatus.Matched, watchRepo.GetById(watch.Id)!.Status);
        Assert.Single(watchRepo.GetById(watch.Id)!.StatusHistory);
    }

    // ── Test 6: Match found → ProductMatchFound event published ──────────────

    [Fact]
    public async Task ScanAsync_PublishesProductMatchFoundEvent_WhenMatchFound()
    {
        var watch = MakeActiveWatch(maxPrice: 500m);
        var listing = MakeListing(price: 400m, seller: "TechZone");
        var watchRepo = new FakeWatchRequestRepository(watch);
        var (_, publisher) = await RunOneScanAsync(watchRepo, [listing]);

        Assert.Single(publisher.PublishedEvents);
        var (evt, topic) = publisher.PublishedEvents[0];
        Assert.IsType<ProductMatchFound>(evt);
        Assert.Equal(TopicNames.ProductMatchFound, topic);

        var matchEvt = (ProductMatchFound)evt;
        Assert.Equal(listing.Name, matchEvt.ProductName);
        Assert.Equal(listing.Price, matchEvt.Price);
        Assert.Equal(listing.Seller, matchEvt.Seller);
        Assert.Equal("product-watch-agent", matchEvt.Source);
        Assert.Equal(watch.Id, matchEvt.CorrelationId);
    }

    // ── Test 7: Multiple listings → best (lowest) price chosen ───────────────

    [Fact]
    public async Task ScanAsync_SelectsBestMatch_WithLowestPrice()
    {
        var watch = MakeActiveWatch(maxPrice: 1000m);
        var (matchRepo, _) = await RunOneScanAsync(
            watches: [watch],
            listings: [MakeListing(price: 500m, seller: "Expensive"), MakeListing(price: 300m, seller: "Cheap")]);

        Assert.Single(matchRepo.CreatedMatches);
        Assert.Equal(300m, matchRepo.CreatedMatches[0].Price);
        Assert.Equal("Cheap", matchRepo.CreatedMatches[0].Seller);
    }

    // ── Test 8: Preferred seller filter applied ───────────────────────────────

    [Fact]
    public async Task ScanAsync_RespectsPreferredSellers_WhenConfigured()
    {
        var watch = MakeActiveWatch(maxPrice: 1000m, preferredSellers: ["TechZone"]);
        var (matchRepo, _) = await RunOneScanAsync(
            watches: [watch],
            listings: [MakeListing(price: 300m, seller: "UnknownSeller"), MakeListing(price: 400m, seller: "TechZone")]);

        Assert.Single(matchRepo.CreatedMatches);
        Assert.Equal("TechZone", matchRepo.CreatedMatches[0].Seller);
    }

    // ── Test 9: Multiple active watches processed independently ──────────────

    [Fact]
    public async Task ScanAsync_ProcessesAllActiveWatches()
    {
        var (matchRepo, publisher) = await RunOneScanAsync(
            watches: [MakeActiveWatch(maxPrice: 500m), MakeActiveWatch(maxPrice: 600m)],
            listings: [MakeListing(price: 450m)]);

        Assert.Equal(2, matchRepo.CreatedMatches.Count);
        Assert.Equal(2, publisher.PublishedEvents.Count);
    }

    // ── Test 10: ProductMatch ExpiresAt set to ~24h from now ─────────────────

    [Fact]
    public async Task ScanAsync_SetsMatchExpiresAt_24HoursFromNow()
    {
        var before = DateTimeOffset.UtcNow;
        var (matchRepo, _) = await RunOneScanAsync(
            watches: [MakeActiveWatch(maxPrice: 500m)],
            listings: [MakeListing(price: 400m)]);
        var after = DateTimeOffset.UtcNow;

        var match = matchRepo.CreatedMatches[0];
        Assert.InRange(match.ExpiresAt, before.AddHours(23.9), after.AddHours(24.1));
    }

    // ── Overloads & core helper ───────────────────────────────────────────────

    private static Task<(FakeProductMatchRepository, FakeEventPublisher)> RunOneScanAsync(
        IEnumerable<WatchRequest> watches,
        IEnumerable<ProductListing> listings)
        => RunOneScanAsync(new FakeWatchRequestRepository(watches.ToArray()), listings);

    private static async Task<(FakeProductMatchRepository matchRepo, FakeEventPublisher publisher)> RunOneScanAsync(
        FakeWatchRequestRepository watchRepo,
        IEnumerable<ProductListing> listings)
    {
        var matchRepo = new FakeProductMatchRepository();
        var productSource = new FakeProductSource(listings.ToList());
        var publisher = new FakeEventPublisher();

        // Use a long poll interval so only one scan fires in the window
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProductWatch:PollIntervalSeconds"] = "60"
            })
            .Build();

        var worker = new ProductWatchWorker(
            watchRepo,
            matchRepo,
            productSource,
            publisher,
            NullLogger<ProductWatchWorker>.Instance,
            config);

        // Start the hosted service; fakes complete synchronously so the first
        // scan finishes well within the 300ms window before StopAsync is called.
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        return (matchRepo, publisher);
    }

    // ── Domain helpers ────────────────────────────────────────────────────────

    private static WatchRequest MakeActiveWatch(decimal maxPrice, string[]? preferredSellers = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            ProductName = "Test Product",
            MaxPrice = maxPrice,
            Status = WatchStatus.Active,
            PreferredSellers = preferredSellers ?? []
        };

    private static ProductListing MakeListing(decimal price, string seller = "TechZone") =>
        new(
            Name: "Test Product",
            Price: price,
            Currency: "USD",
            Seller: seller,
            Url: "https://store.example.com/test",
            Availability: ProductAvailability.InStock);
}
