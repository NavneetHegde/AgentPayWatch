using System.Net;
using System.Net.Http.Json;
using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using NSubstitute;
using Xunit;

namespace AgentPayWatch.Api.Tests;

public sealed class WatchEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public WatchEndpointsTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static WatchRequest BuildWatch(string userId = "user-1") => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        ProductName = "Laptop",
        MaxPrice = 999.99m,
        Currency = "USD",
        PreferredSellers = [],
        Status = WatchStatus.Active
    };

    private static WatchRequest BuildPausedWatch(string userId = "user-1")
    {
        var w = BuildWatch(userId);
        w.UpdateStatus(WatchStatus.Paused, "paused for test");
        return w;
    }

    private static WatchRequest BuildCancelledWatch(string userId = "user-1")
    {
        var w = BuildWatch(userId);
        w.UpdateStatus(WatchStatus.Cancelled, "cancelled for test");
        return w;
    }

    // Builds a watch in Purchasing state — cannot be Cancelled (tests invalid transition)
    private static WatchRequest BuildPurchasingWatch(string userId = "user-1")
    {
        var w = BuildWatch(userId);
        w.UpdateStatus(WatchStatus.Matched, "matched");
        w.UpdateStatus(WatchStatus.AwaitingApproval, "awaiting approval");
        w.UpdateStatus(WatchStatus.Approved, "approved");
        w.UpdateStatus(WatchStatus.Purchasing, "purchasing");
        return w;
    }

    // ── GET / (root) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Root_ReturnsApiLabel()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("AgentPay Watch Api", body);
    }

    // ── POST /api/watches ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateWatch_ValidRequest_Returns201WithBody()
    {
        var watch = BuildWatch();
        _factory.WatchRepository
            .CreateAsync(Arg.Any<WatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(watch);

        var request = new CreateWatchRequest("Laptop", 999.99m, "USD", ["BestBuy"]);
        var response = await _client.PostAsJsonAsync("/api/watches?userId=user-1", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WatchResponse>();
        Assert.NotNull(body);
        Assert.Equal("Laptop", body.ProductName);
        Assert.Equal(999.99m, body.MaxPrice);
        Assert.Equal(WatchStatus.Active, body.Status);
    }

    [Fact]
    public async Task CreateWatch_MissingUserId_Returns400()
    {
        var request = new CreateWatchRequest("Laptop", 999.99m);
        var response = await _client.PostAsJsonAsync("/api/watches", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateWatch_EmptyProductName_Returns400()
    {
        var request = new CreateWatchRequest("", 999.99m);
        var response = await _client.PostAsJsonAsync("/api/watches?userId=user-1", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ProductName", body);
    }

    [Fact]
    public async Task CreateWatch_ZeroMaxPrice_Returns400()
    {
        var request = new CreateWatchRequest("Laptop", 0m);
        var response = await _client.PostAsJsonAsync("/api/watches?userId=user-1", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MaxPrice", body);
    }

    [Fact]
    public async Task CreateWatch_NegativeMaxPrice_Returns400()
    {
        var request = new CreateWatchRequest("Laptop", -1m);
        var response = await _client.PostAsJsonAsync("/api/watches?userId=user-1", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateWatch_WhitespaceProductName_Returns400()
    {
        var request = new CreateWatchRequest("   ", 999.99m);
        var response = await _client.PostAsJsonAsync("/api/watches?userId=user-1", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ProductName", body);
    }

    [Fact]
    public async Task CreateWatch_ResponseContainsCorrectUserId()
    {
        var watch = BuildWatch("user-42");
        _factory.WatchRepository
            .CreateAsync(Arg.Any<WatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(watch);

        var request = new CreateWatchRequest("Laptop", 999.99m);
        var response = await _client.PostAsJsonAsync("/api/watches?userId=user-42", request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WatchResponse>();
        Assert.NotNull(body);
        Assert.Equal("user-42", body.UserId);
    }

    // ── GET /api/watches ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListWatches_ValidUserId_Returns200WithList()
    {
        var watches = new List<WatchRequest> { BuildWatch(), BuildWatch() };
        _factory.WatchRepository
            .GetByUserIdAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(watches);

        var response = await _client.GetAsync("/api/watches?userId=user-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WatchResponse[]>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Length);
    }

    [Fact]
    public async Task ListWatches_EmptyList_Returns200WithEmptyArray()
    {
        _factory.WatchRepository
            .GetByUserIdAsync("user-empty", Arg.Any<CancellationToken>())
            .Returns(new List<WatchRequest>());

        var response = await _client.GetAsync("/api/watches?userId=user-empty");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WatchResponse[]>();
        Assert.NotNull(body);
        Assert.Empty(body);
    }

    [Fact]
    public async Task ListWatches_MissingUserId_Returns400()
    {
        var response = await _client.GetAsync("/api/watches");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── GET /api/watches/{id} ─────────────────────────────────────────────────

    [Fact]
    public async Task GetWatch_ExistingId_Returns200WithBody()
    {
        var watch = BuildWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);

        var response = await _client.GetAsync($"/api/watches/{watch.Id}?userId=user-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WatchResponse>();
        Assert.NotNull(body);
        Assert.Equal(watch.Id, body.Id);
    }

    [Fact]
    public async Task GetWatch_NonExistentId_Returns404()
    {
        var missingId = Guid.NewGuid();
        _factory.WatchRepository
            .GetByIdAsync(missingId, "user-1", Arg.Any<CancellationToken>())
            .Returns((WatchRequest?)null);

        var response = await _client.GetAsync($"/api/watches/{missingId}?userId=user-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetWatch_MissingUserId_Returns400()
    {
        var response = await _client.GetAsync($"/api/watches/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── PUT /api/watches/{id}/pause ───────────────────────────────────────────

    [Fact]
    public async Task PauseWatch_ActiveWatch_Returns200WithPausedStatus()
    {
        var watch = BuildWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);
        _factory.WatchRepository
            .UpdateAsync(Arg.Any<WatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PutAsync($"/api/watches/{watch.Id}/pause?userId=user-1", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WatchResponse>();
        Assert.NotNull(body);
        Assert.Equal(WatchStatus.Paused, body.Status);
    }

    [Fact]
    public async Task PauseWatch_NonExistentId_Returns404()
    {
        var missingId = Guid.NewGuid();
        _factory.WatchRepository
            .GetByIdAsync(missingId, "user-1", Arg.Any<CancellationToken>())
            .Returns((WatchRequest?)null);

        var response = await _client.PutAsync($"/api/watches/{missingId}/pause?userId=user-1", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PauseWatch_MissingUserId_Returns400()
    {
        var response = await _client.PutAsync($"/api/watches/{Guid.NewGuid()}/pause", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PauseWatch_AlreadyPausedWatch_Returns200WithPausedStatus()
    {
        var watch = BuildPausedWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);
        _factory.WatchRepository
            .UpdateAsync(Arg.Any<WatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PutAsync($"/api/watches/{watch.Id}/pause?userId=user-1", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WatchResponse>();
        Assert.NotNull(body);
        Assert.Equal(WatchStatus.Paused, body.Status);
    }

    [Fact]
    public async Task PauseWatch_InvalidStateTransition_Returns400()
    {
        var watch = BuildCancelledWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);

        var response = await _client.PutAsync($"/api/watches/{watch.Id}/pause?userId=user-1", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Cancelled", body);
    }

    [Fact]
    public async Task PauseWatch_RecordsStatusHistory()
    {
        var watch = BuildWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);

        WatchRequest? saved = null;
        _factory.WatchRepository
            .UpdateAsync(Arg.Do<WatchRequest>(w => saved = w), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _client.PutAsync($"/api/watches/{watch.Id}/pause?userId=user-1", null);

        Assert.NotNull(saved);
        Assert.Single(saved.StatusHistory);
        Assert.Equal(WatchStatus.Active, saved.StatusHistory[0].From);
        Assert.Equal(WatchStatus.Paused, saved.StatusHistory[0].To);
    }

    // ── PUT /api/watches/{id}/resume ──────────────────────────────────────────

    [Fact]
    public async Task ResumeWatch_PausedWatch_Returns200WithActiveStatus()
    {
        var watch = BuildPausedWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);
        _factory.WatchRepository
            .UpdateAsync(Arg.Any<WatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PutAsync($"/api/watches/{watch.Id}/resume?userId=user-1", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WatchResponse>();
        Assert.NotNull(body);
        Assert.Equal(WatchStatus.Active, body.Status);
    }

    [Fact]
    public async Task ResumeWatch_NonExistentId_Returns404()
    {
        var missingId = Guid.NewGuid();
        _factory.WatchRepository
            .GetByIdAsync(missingId, "user-1", Arg.Any<CancellationToken>())
            .Returns((WatchRequest?)null);

        var response = await _client.PutAsync($"/api/watches/{missingId}/resume?userId=user-1", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResumeWatch_MissingUserId_Returns400()
    {
        var response = await _client.PutAsync($"/api/watches/{Guid.NewGuid()}/resume", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ResumeWatch_AlreadyActiveWatch_Returns200WithActiveStatus()
    {
        var watch = BuildWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);
        _factory.WatchRepository
            .UpdateAsync(Arg.Any<WatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PutAsync($"/api/watches/{watch.Id}/resume?userId=user-1", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<WatchResponse>();
        Assert.NotNull(body);
        Assert.Equal(WatchStatus.Active, body.Status);
    }

    [Fact]
    public async Task ResumeWatch_InvalidStateTransition_Returns400()
    {
        var watch = BuildCancelledWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);

        var response = await _client.PutAsync($"/api/watches/{watch.Id}/resume?userId=user-1", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Cancelled", body);
    }

    // ── DELETE /api/watches/{id} ──────────────────────────────────────────────

    [Fact]
    public async Task CancelWatch_ExistingWatch_Returns204()
    {
        var watch = BuildWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);
        _factory.WatchRepository
            .UpdateAsync(Arg.Any<WatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.DeleteAsync($"/api/watches/{watch.Id}?userId=user-1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CancelWatch_NonExistentId_Returns404()
    {
        var missingId = Guid.NewGuid();
        _factory.WatchRepository
            .GetByIdAsync(missingId, "user-1", Arg.Any<CancellationToken>())
            .Returns((WatchRequest?)null);

        var response = await _client.DeleteAsync($"/api/watches/{missingId}?userId=user-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CancelWatch_MissingUserId_Returns400()
    {
        var response = await _client.DeleteAsync($"/api/watches/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CancelWatch_AlreadyCancelledWatch_Returns204()
    {
        var watch = BuildCancelledWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);
        _factory.WatchRepository
            .UpdateAsync(Arg.Any<WatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.DeleteAsync($"/api/watches/{watch.Id}?userId=user-1");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task CancelWatch_InvalidStateTransition_Returns400()
    {
        // Purchasing → Cancelled is not an allowed transition
        var watch = BuildPurchasingWatch();
        _factory.WatchRepository
            .GetByIdAsync(watch.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(watch);

        var response = await _client.DeleteAsync($"/api/watches/{watch.Id}?userId=user-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Purchasing", body);
    }
}
