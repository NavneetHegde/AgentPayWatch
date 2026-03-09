using System.Net;
using System.Net.Http.Json;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using NSubstitute;
using Xunit;

namespace AgentPayWatch.Api.Tests;

public sealed class MatchEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public MatchEndpointsTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── GET /api/matches/{watchId} ────────────────────────────────────────────

    [Fact]
    public async Task GetMatches_NoMatches_Returns200WithEmptyList()
    {
        var watchId = Guid.NewGuid();
        _factory.MatchRepository
            .GetByWatchRequestIdAsync(watchId, Arg.Any<CancellationToken>())
            .Returns(new List<ProductMatch>());

        var response = await _client.GetAsync($"/api/matches/{watchId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MatchResponseDto[]>();
        Assert.NotNull(body);
        Assert.Empty(body);
    }

    [Fact]
    public async Task GetMatches_MatchWithNoApproval_ReturnsNullApprovalFields()
    {
        var match = BuildMatch();
        _factory.MatchRepository
            .GetByWatchRequestIdAsync(match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns(new List<ProductMatch> { match });
        _factory.ApprovalRepository
            .GetByMatchIdAsync(match.Id, match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns((ApprovalRecord?)null);

        var response = await _client.GetAsync($"/api/matches/{match.WatchRequestId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MatchResponseDto[]>();
        Assert.NotNull(body);
        var item = Assert.Single(body);
        Assert.Null(item.ApprovalToken);
        Assert.Null(item.ApprovalDecision);
        Assert.Null(item.ApprovalExpiresAt);
    }

    [Fact]
    public async Task GetMatches_MatchWithPendingApproval_ExposesToken()
    {
        var match = BuildMatch();
        var approval = BuildApproval(match, ApprovalDecision.Pending, expiresAt: DateTimeOffset.UtcNow.AddMinutes(10));

        _factory.MatchRepository
            .GetByWatchRequestIdAsync(match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns(new List<ProductMatch> { match });
        _factory.ApprovalRepository
            .GetByMatchIdAsync(match.Id, match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns(approval);

        var response = await _client.GetAsync($"/api/matches/{match.WatchRequestId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MatchResponseDto[]>();
        Assert.NotNull(body);
        var item = Assert.Single(body);
        Assert.Equal(approval.ApprovalToken, item.ApprovalToken);
        Assert.Equal("Pending", item.ApprovalDecision);
        Assert.NotNull(item.ApprovalExpiresAt);
    }

    [Fact]
    public async Task GetMatches_MatchWithExpiredPendingApproval_HidesToken()
    {
        var match = BuildMatch();
        // Decision is still Pending but ExpiresAt is in the past
        var approval = BuildApproval(match, ApprovalDecision.Pending, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        _factory.MatchRepository
            .GetByWatchRequestIdAsync(match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns(new List<ProductMatch> { match });
        _factory.ApprovalRepository
            .GetByMatchIdAsync(match.Id, match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns(approval);

        var response = await _client.GetAsync($"/api/matches/{match.WatchRequestId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MatchResponseDto[]>();
        Assert.NotNull(body);
        var item = Assert.Single(body);
        Assert.Null(item.ApprovalToken);          // token hidden
        Assert.Equal("Pending", item.ApprovalDecision); // decision still shown
    }

    [Fact]
    public async Task GetMatches_MatchWithApprovedDecision_HidesToken()
    {
        var match = BuildMatch();
        var approval = BuildApproval(match, ApprovalDecision.Approved, expiresAt: DateTimeOffset.UtcNow.AddMinutes(5));

        _factory.MatchRepository
            .GetByWatchRequestIdAsync(match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns(new List<ProductMatch> { match });
        _factory.ApprovalRepository
            .GetByMatchIdAsync(match.Id, match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns(approval);

        var response = await _client.GetAsync($"/api/matches/{match.WatchRequestId}");

        var body = await response.Content.ReadFromJsonAsync<MatchResponseDto[]>();
        Assert.NotNull(body);
        var item = Assert.Single(body);
        Assert.Null(item.ApprovalToken);          // token never exposed for resolved approvals
        Assert.Equal("Approved", item.ApprovalDecision);
    }

    [Fact]
    public async Task GetMatches_MultipleMatches_ReturnsAll()
    {
        var watchId = Guid.NewGuid();
        var match1 = BuildMatch(watchId);
        var match2 = BuildMatch(watchId);

        _factory.MatchRepository
            .GetByWatchRequestIdAsync(watchId, Arg.Any<CancellationToken>())
            .Returns(new List<ProductMatch> { match1, match2 });
        _factory.ApprovalRepository
            .GetByMatchIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ApprovalRecord?)null);

        var response = await _client.GetAsync($"/api/matches/{watchId}");

        var body = await response.Content.ReadFromJsonAsync<MatchResponseDto[]>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Length);
    }

    [Fact]
    public async Task GetMatches_ReturnsCorrectMatchFields()
    {
        var match = BuildMatch();
        _factory.MatchRepository
            .GetByWatchRequestIdAsync(match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns(new List<ProductMatch> { match });
        _factory.ApprovalRepository
            .GetByMatchIdAsync(match.Id, match.WatchRequestId, Arg.Any<CancellationToken>())
            .Returns((ApprovalRecord?)null);

        var response = await _client.GetAsync($"/api/matches/{match.WatchRequestId}");

        var body = await response.Content.ReadFromJsonAsync<MatchResponseDto[]>();
        Assert.NotNull(body);
        var item = Assert.Single(body);
        Assert.Equal(match.Id, item.Id);
        Assert.Equal(match.WatchRequestId, item.WatchRequestId);
        Assert.Equal(match.ProductName, item.ProductName);
        Assert.Equal(match.Price, item.Price);
        Assert.Equal(match.Seller, item.Seller);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProductMatch BuildMatch(Guid? watchRequestId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            WatchRequestId = watchRequestId ?? Guid.NewGuid(),
            UserId = "user-1",
            ProductName = "Test Product",
            Price = 399.99m,
            Currency = "USD",
            Seller = "TechZone",
            ProductUrl = "https://store.example.com/test",
            MatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            Availability = ProductAvailability.InStock
        };

    private static ApprovalRecord BuildApproval(
        ProductMatch match,
        ApprovalDecision decision,
        DateTimeOffset expiresAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            WatchRequestId = match.WatchRequestId,
            UserId = match.UserId,
            ApprovalToken = Guid.NewGuid().ToString("N"),
            SentAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ExpiresAt = expiresAt,
            Decision = decision,
            Channel = NotificationChannel.A2P_SMS
        };
}

// ── Response DTO for deserialization ─────────────────────────────────────────

file sealed record MatchResponseDto(
    Guid Id,
    Guid WatchRequestId,
    string UserId,
    string ProductName,
    decimal Price,
    string Currency,
    string Seller,
    string ProductUrl,
    DateTimeOffset MatchedAt,
    DateTimeOffset ExpiresAt,
    int Availability,
    string? ApprovalToken,
    string? ApprovalDecision,
    DateTimeOffset? ApprovalExpiresAt
);
