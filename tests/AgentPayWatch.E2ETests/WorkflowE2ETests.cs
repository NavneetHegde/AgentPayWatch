using System.Net;
using System.Net.Http.Json;
using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Api.Endpoints;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Models;
using AgentPayWatch.Infrastructure.Messaging;
using Xunit;

namespace AgentPayWatch.E2ETests;

/// <summary>
/// End-to-end tests that exercise the full agent workflow through the API layer,
/// using shared in-memory repositories so every component sees the same state.
///
/// Each test creates its own <see cref="E2ETestFactory"/> (and disposes it) to keep
/// in-memory state fully isolated between tests.
///
/// Workflow under test:
///   POST /api/watches  (Active)
///       → ProductWatchAgent finds a match  (Matched)
///       → ApprovalAgent sends A2P message  (AwaitingApproval)
///       → POST /api/a2p/callback  (Approved | Active)
///       → PaymentAgent executes payment  (Completed | Active on failure)
/// </summary>
public sealed class WorkflowE2ETests
{
    private const string UserId = "e2e-user-1";

    // ── Happy Path: BUY approval ──────────────────────────────────────────────

    [Fact]
    public async Task FullWorkflow_BuyApproval_WatchEndsAsCompleted()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Sony WH-1000XM5", 279.99m, "USD", "AudioHaven",
                "https://store.example.com/sony", ProductAvailability.InStock));
        ctx.Factory.PaymentProvider.SetSuccess();

        // 1. Create watch via API
        var watchId = await ctx.CreateWatchAsync("Sony WH-1000XM5", maxPrice: 300m);
        ctx.AssertWatchStatus(watchId, WatchStatus.Active);

        // 2. ProductWatch agent scans — finds the match
        await ctx.Simulator.RunProductWatchScanAsync();
        ctx.AssertWatchStatus(watchId, WatchStatus.Matched);

        // 3. Approval agent sends A2P message
        await ctx.Simulator.RunApprovalProcessingAsync();
        ctx.AssertWatchStatus(watchId, WatchStatus.AwaitingApproval);

        // 4. User responds BUY via callback — use the token tracked for this watch
        var token = ctx.GetApprovalTokenForWatch(watchId);
        Assert.NotNull(token);
        var callbackResp = await ctx.PostCallbackAsync(token, "BUY");
        Assert.Equal(HttpStatusCode.OK, callbackResp.StatusCode);
        ctx.AssertWatchStatus(watchId, WatchStatus.Approved);

        // Callback published ApprovalDecided event
        var decidedEvent = Assert.Single(
            ctx.Factory.EventPublisher.EventsOfType<ApprovalDecided>(),
            e => e.CorrelationId == watchId);
        Assert.Equal(ApprovalDecision.Approved, decidedEvent.Decision);

        // 5. Payment agent processes the approved event
        await ctx.Simulator.RunPaymentAsync();
        ctx.AssertWatchStatus(watchId, WatchStatus.Completed);

        // 6. Transaction created and visible via the API
        var transactions = await ctx.GetTransactionsAsync(UserId);
        var tx = Assert.Single(transactions, t => t.WatchRequestId == watchId);
        Assert.Equal("Succeeded", tx.Status);
        Assert.Equal(279.99m, tx.Amount);
        Assert.Equal("AudioHaven", tx.Merchant);

        // 7. PaymentCompleted event was published
        Assert.Single(
            ctx.Factory.EventPublisher.EventsOfType<PaymentCompleted>(),
            e => e.CorrelationId == watchId);
    }

    // ── Happy Path: SKIP rejection ────────────────────────────────────────────

    [Fact]
    public async Task FullWorkflow_SkipDecision_WatchReturnsToActive()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("PlayStation 5 Console", 429.00m, "USD", "GameVault",
                "https://store.example.com/ps5", ProductAvailability.LimitedStock));

        var watchId = await ctx.CreateWatchAsync("PlayStation 5 Console", maxPrice: 450m);

        await ctx.Simulator.RunProductWatchScanAsync();
        await ctx.Simulator.RunApprovalProcessingAsync();
        ctx.AssertWatchStatus(watchId, WatchStatus.AwaitingApproval);

        // User responds SKIP
        var token = ctx.GetApprovalTokenForWatch(watchId);
        var callbackResp = await ctx.PostCallbackAsync(token!, "SKIP");
        Assert.Equal(HttpStatusCode.OK, callbackResp.StatusCode);

        // Watch returns to Active
        ctx.AssertWatchStatus(watchId, WatchStatus.Active);

        // ApprovalDecided event has Rejected decision
        var decidedEvent = Assert.Single(
            ctx.Factory.EventPublisher.EventsOfType<ApprovalDecided>(),
            e => e.CorrelationId == watchId);
        Assert.Equal(ApprovalDecision.Rejected, decidedEvent.Decision);

        // No payment events for this watch
        Assert.DoesNotContain(
            ctx.Factory.EventPublisher.EventsOfType<PaymentCompleted>(),
            e => e.CorrelationId == watchId);
    }

    // ── Happy Path: Payment failure → watch retried ───────────────────────────

    [Fact]
    public async Task FullWorkflow_PaymentFailure_WatchReturnsToActive()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Xbox Series X", 399.00m, "USD", "TechZone",
                "https://store.example.com/xbox", ProductAvailability.InStock));
        ctx.Factory.PaymentProvider.SetFailure("Insufficient funds");

        var watchId = await ctx.CreateWatchAsync("Xbox Series X", maxPrice: 450m);

        await ctx.Simulator.RunProductWatchScanAsync();
        await ctx.Simulator.RunApprovalProcessingAsync();
        await ctx.PostCallbackAsync(ctx.GetApprovalTokenForWatch(watchId)!, "BUY");
        ctx.AssertWatchStatus(watchId, WatchStatus.Approved);

        // Payment agent tries and fails
        await ctx.Simulator.RunPaymentAsync();

        // Watch is back to Active so it can be re-scanned
        ctx.AssertWatchStatus(watchId, WatchStatus.Active);

        // Failed transaction was recorded
        var transactions = await ctx.GetTransactionsAsync(UserId);
        var tx = Assert.Single(transactions, t => t.WatchRequestId == watchId);
        Assert.Equal("Failed", tx.Status);
        Assert.Equal("Insufficient funds", tx.FailureReason);

        // PaymentFailed event was published
        Assert.Single(
            ctx.Factory.EventPublisher.EventsOfType<PaymentFailed>(),
            e => e.CorrelationId == watchId);
    }

    // ── No match — watch stays Active ─────────────────────────────────────────

    [Fact]
    public async Task ProductWatchScan_NoMatch_WatchRemainsActive()
    {
        await using var ctx = new TestContext();
        // Product price exceeds max budget
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("iPhone 15 Pro", 1099.00m, "USD", "TechZone",
                "https://store.example.com/iphone15pro", ProductAvailability.InStock));

        var watchId = await ctx.CreateWatchAsync("iPhone 15 Pro", maxPrice: 900m);

        await ctx.Simulator.RunProductWatchScanAsync();

        ctx.AssertWatchStatus(watchId, WatchStatus.Active);
        Assert.DoesNotContain(
            ctx.Factory.EventPublisher.EventsOfType<ProductMatchFound>(),
            e => e.CorrelationId == watchId);
    }

    // ── No listings at all ────────────────────────────────────────────────────

    [Fact]
    public async Task ProductWatchScan_NoListings_WatchRemainsActive()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.Clear();

        var watchId = await ctx.CreateWatchAsync("Unavailable Widget", maxPrice: 50m);

        await ctx.Simulator.RunProductWatchScanAsync();

        ctx.AssertWatchStatus(watchId, WatchStatus.Active);
    }

    // ── Preferred seller filter ───────────────────────────────────────────────

    [Fact]
    public async Task ProductWatchScan_WrongSeller_DoesNotMatch()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Nintendo Switch OLED", 279.00m, "USD", "WrongStore",
                "https://store.example.com/switch", ProductAvailability.InStock));

        var watchId = await ctx.CreateWatchAsync("Nintendo Switch OLED", maxPrice: 300m,
            preferredSellers: ["GameVault"]);

        await ctx.Simulator.RunProductWatchScanAsync();

        ctx.AssertWatchStatus(watchId, WatchStatus.Active);
    }

    [Fact]
    public async Task ProductWatchScan_CorrectSeller_Matches()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Nintendo Switch OLED", 279.00m, "USD", "GameVault",
                "https://store.example.com/switch", ProductAvailability.InStock));

        var watchId = await ctx.CreateWatchAsync("Nintendo Switch OLED", maxPrice: 300m,
            preferredSellers: ["GameVault"]);

        await ctx.Simulator.RunProductWatchScanAsync();

        ctx.AssertWatchStatus(watchId, WatchStatus.Matched);
    }

    // ── Pause / Resume ────────────────────────────────────────────────────────

    [Fact]
    public async Task PausedWatch_IsNotScanned_ByProductWatchAgent()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("KitchenAid Stand Mixer", 299.00m, "USD", "HomeEssentials",
                "https://store.example.com/kitchenaid", ProductAvailability.InStock));

        var watchId = await ctx.CreateWatchAsync("KitchenAid Stand Mixer", maxPrice: 350m);

        // Pause via API
        var pauseResp = await ctx.Client.PutAsync($"/api/watches/{watchId}/pause?userId={UserId}", null);
        Assert.Equal(HttpStatusCode.OK, pauseResp.StatusCode);
        ctx.AssertWatchStatus(watchId, WatchStatus.Paused);

        // Paused watch is not scanned
        await ctx.Simulator.RunProductWatchScanAsync();
        ctx.AssertWatchStatus(watchId, WatchStatus.Paused);

        // Resume via API
        var resumeResp = await ctx.Client.PutAsync($"/api/watches/{watchId}/resume?userId={UserId}", null);
        Assert.Equal(HttpStatusCode.OK, resumeResp.StatusCode);
        ctx.AssertWatchStatus(watchId, WatchStatus.Active);

        // Now the scan picks it up
        await ctx.Simulator.RunProductWatchScanAsync();
        ctx.AssertWatchStatus(watchId, WatchStatus.Matched);
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelledWatch_IsNotScanned_ByProductWatchAgent()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Clean Architecture Book", 29.00m, "USD", "BookNest",
                "https://store.example.com/clean-arch", ProductAvailability.InStock));

        var watchId = await ctx.CreateWatchAsync("Clean Architecture Book", maxPrice: 35m);

        var cancelResp = await ctx.Client.DeleteAsync($"/api/watches/{watchId}?userId={UserId}");
        Assert.Equal(HttpStatusCode.NoContent, cancelResp.StatusCode);

        await ctx.Simulator.RunProductWatchScanAsync();

        ctx.AssertWatchStatus(watchId, WatchStatus.Cancelled);
    }

    // ── Callback validation ───────────────────────────────────────────────────

    [Fact]
    public async Task Callback_UnknownToken_Returns404()
    {
        await using var ctx = new TestContext();
        var response = await ctx.PostCallbackAsync("invalid-token-that-does-not-exist", "BUY");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Callback_AlreadyResolved_Returns400()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Sony WH-1000XM5", 249.00m, "USD", "AudioHaven",
                "https://store.example.com/sony", ProductAvailability.InStock));
        ctx.Factory.PaymentProvider.SetSuccess();

        var watchId = await ctx.CreateWatchAsync("Sony WH-1000XM5", maxPrice: 260m);
        await ctx.Simulator.RunProductWatchScanAsync();
        await ctx.Simulator.RunApprovalProcessingAsync();

        var token = ctx.GetApprovalTokenForWatch(watchId)!;

        // First call succeeds
        var first = await ctx.PostCallbackAsync(token, "BUY");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Second call with same token should fail
        var second = await ctx.PostCallbackAsync(token, "BUY");
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Callback_InvalidDecision_Returns400()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Sony WH-1000XM5", 249.00m, "USD", "AudioHaven",
                "https://store.example.com/sony", ProductAvailability.InStock));

        var watchId = await ctx.CreateWatchAsync("Sony WH-1000XM5", maxPrice: 260m);
        await ctx.Simulator.RunProductWatchScanAsync();
        await ctx.Simulator.RunApprovalProcessingAsync();

        var token = ctx.GetApprovalTokenForWatch(watchId)!;

        var response = await ctx.PostCallbackAsync(token, "MAYBE");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Transaction visibility ────────────────────────────────────────────────

    [Fact]
    public async Task GetTransaction_ById_ReturnsCorrectDetails()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Designing Data-Intensive Applications", 35.00m, "USD", "PageTurner",
                "https://store.example.com/ddia", ProductAvailability.InStock));
        ctx.Factory.PaymentProvider.SetSuccess();

        var watchId = await ctx.CreateWatchAsync("Designing Data-Intensive Applications", maxPrice: 40m);
        await ctx.Simulator.RunProductWatchScanAsync();
        await ctx.Simulator.RunApprovalProcessingAsync();
        await ctx.PostCallbackAsync(ctx.GetApprovalTokenForWatch(watchId)!, "BUY");
        await ctx.Simulator.RunPaymentAsync();

        var transactions = await ctx.GetTransactionsAsync(UserId);
        var tx = transactions.Single(t => t.WatchRequestId == watchId);

        // Fetch by ID
        var byId = await ctx.GetTransactionByIdAsync(tx.Id);
        Assert.NotNull(byId);
        Assert.Equal(tx.Id, byId.Id);
        Assert.Equal("Succeeded", byId.Status);
        Assert.Equal(35.00m, byId.Amount);
    }

    // ── Multi-watch concurrency ───────────────────────────────────────────────

    [Fact]
    public async Task TwoActiveWatches_BothMatchAndPurchase_Independently()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Clean Architecture Book", 29.00m, "USD", "BookNest",
                "https://store.example.com/clean-arch", ProductAvailability.InStock),
            new ProductListing("PlayStation 5 Console", 399.00m, "USD", "GameVault",
                "https://store.example.com/ps5", ProductAvailability.LimitedStock));
        ctx.Factory.PaymentProvider.SetSuccess();

        const string userId2 = "e2e-user-multi";

        var watchId1 = await ctx.CreateWatchAsync("Clean Architecture Book", maxPrice: 35m);
        var watchId2 = await ctx.CreateWatchAsync("PlayStation 5 Console", maxPrice: 450m, userId: userId2);

        // Both watches get scanned and matched
        await ctx.Simulator.RunProductWatchScanAsync();
        ctx.AssertWatchStatus(watchId1, WatchStatus.Matched);
        ctx.AssertWatchStatus(watchId2, WatchStatus.Matched);

        // Both get approval records
        await ctx.Simulator.RunApprovalProcessingAsync();
        ctx.AssertWatchStatus(watchId1, WatchStatus.AwaitingApproval);
        ctx.AssertWatchStatus(watchId2, WatchStatus.AwaitingApproval);

        // Both users respond BUY
        await ctx.PostCallbackAsync(ctx.GetApprovalTokenForWatch(watchId1)!, "BUY");
        await ctx.PostCallbackAsync(ctx.GetApprovalTokenForWatch(watchId2)!, "BUY");

        // Payment agent processes each approved event
        var approvedEvents = ctx.Factory.EventPublisher
            .EventsOfType<ApprovalDecided>()
            .Where(e => e.Decision == ApprovalDecision.Approved)
            .ToList();
        Assert.Equal(2, approvedEvents.Count);

        foreach (var evt in approvedEvents)
            await ctx.Simulator.RunPaymentAsync(evt);

        ctx.AssertWatchStatus(watchId1, WatchStatus.Completed);
        ctx.AssertWatchStatus(watchId2, WatchStatus.Completed);
    }

    // ── Matches endpoint ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetMatches_AfterApprovalProcessing_ReturnsMatchWithPendingToken()
    {
        await using var ctx = new TestContext();
        ctx.Factory.ProductSource.SetListings(
            new ProductListing("Xbox Series X", 389.00m, "USD", "TechZone",
                "https://store.example.com/xbox", ProductAvailability.InStock));

        var watchId = await ctx.CreateWatchAsync("Xbox Series X", maxPrice: 400m);
        await ctx.Simulator.RunProductWatchScanAsync();
        await ctx.Simulator.RunApprovalProcessingAsync();

        var response = await ctx.Client.GetAsync($"/api/matches/{watchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var matches = await response.Content
            .ReadFromJsonAsync<MatchResponse[]>(E2ETestFactory.JsonOptions);

        Assert.NotNull(matches);
        var match = Assert.Single(matches);
        Assert.Equal("Xbox Series X", match.ProductName);
        Assert.Equal("Pending", match.ApprovalDecision);
        Assert.NotNull(match.ApprovalToken); // exposed while pending and not expired
    }

    // ── Per-test context helper ───────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh <see cref="E2ETestFactory"/> and <see cref="AgentSimulator"/>
    /// for each test, ensuring complete state isolation.
    /// </summary>
    private sealed class TestContext : IAsyncDisposable
    {
        public E2ETestFactory Factory { get; } = new();
        public HttpClient Client { get; }
        public AgentSimulator Simulator { get; }

        public TestContext()
        {
            Client = Factory.CreateClient();
            Simulator = Factory.CreateAgentSimulator();
        }

        public async Task<Guid> CreateWatchAsync(
            string productName,
            decimal maxPrice,
            string[]? preferredSellers = null,
            string? userId = null)
        {
            var request = new CreateWatchRequest(productName, maxPrice, "USD", preferredSellers);
            var uid = userId ?? UserId;
            var response = await Client.PostAsJsonAsync($"/api/watches?userId={uid}", request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = await response.Content
                .ReadFromJsonAsync<WatchResponse>(E2ETestFactory.JsonOptions);
            Assert.NotNull(body);
            return body.Id;
        }

        public void AssertWatchStatus(Guid watchId, WatchStatus expected)
        {
            var watch = Factory.WatchRepo.GetByIdAsync(watchId).GetAwaiter().GetResult();
            Assert.NotNull(watch);
            Assert.Equal(expected, watch.Status);
        }

        /// <summary>
        /// Retrieves the approval token sent to the user for a specific watch,
        /// looked up from the shared ApprovalRepo (not from LastApprovalToken) to
        /// prevent interference when multiple watches exist in the same test.
        /// </summary>
        public string? GetApprovalTokenForWatch(Guid watchId)
        {
            // Find the match for this watch, then find the approval for that match
            var matches = Factory.MatchRepo
                .GetByWatchRequestIdAsync(watchId)
                .GetAwaiter().GetResult();

            var match = matches.FirstOrDefault();
            if (match is null) return null;

            var approval = Factory.ApprovalRepo
                .GetByMatchIdAsync(match.Id, watchId)
                .GetAwaiter().GetResult();

            return approval?.ApprovalToken;
        }

        public Task<HttpResponseMessage> PostCallbackAsync(string token, string decision) =>
            Client.PostAsJsonAsync("/api/a2p/callback", new ApprovalCallbackRequest(token, decision));

        public async Task<TransactionResponse[]> GetTransactionsAsync(string userId)
        {
            var response = await Client.GetAsync($"/api/transactions?userId={userId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content
                .ReadFromJsonAsync<TransactionResponse[]>(E2ETestFactory.JsonOptions)
                ?? [];
        }

        public async Task<TransactionResponse?> GetTransactionByIdAsync(Guid id)
        {
            var tx = Factory.TransactionRepo.All.FirstOrDefault(t => t.Id == id);
            if (tx is null) return null;

            var response = await Client.GetAsync($"/api/transactions/{id}?userId={tx.UserId}");
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            return await response.Content
                .ReadFromJsonAsync<TransactionResponse>(E2ETestFactory.JsonOptions);
        }

        public ValueTask DisposeAsync()
        {
            Client.Dispose();
            return Factory.DisposeAsync();
        }
    }
}
