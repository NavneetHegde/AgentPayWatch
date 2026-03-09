using System.Net;
using System.Net.Http.Json;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Infrastructure.Messaging;
using NSubstitute;
using Xunit;

namespace AgentPayWatch.Api.Tests;

public sealed class CallbackEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public CallbackEndpointsTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── POST /api/a2p/callback ────────────────────────────────────────────────

    [Fact]
    public async Task Callback_BuyDecision_Returns200WithApprovedDecision()
    {
        var (approval, watch) = BuildPendingApproval();
        SetupApprovalMocks(approval, watch);

        var response = await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "BUY" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CallbackResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Approved", body.Decision);
        Assert.Equal(approval.Id, body.ApprovalId);
        Assert.Equal(watch.Id, body.WatchRequestId);
    }

    [Fact]
    public async Task Callback_SkipDecision_Returns200WithRejectedDecision()
    {
        var (approval, watch) = BuildPendingApproval();
        SetupApprovalMocks(approval, watch);

        var response = await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "SKIP" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CallbackResponseDto>();
        Assert.NotNull(body);
        Assert.Equal("Rejected", body.Decision);
    }

    [Fact]
    public async Task Callback_BuyDecision_UpdatesWatchStatusToApproved()
    {
        var (approval, watch) = BuildPendingApproval();
        SetupApprovalMocks(approval, watch);

        WatchRequest? saved = null;
        _factory.WatchRepository
            .UpdateAsync(Arg.Do<WatchRequest>(w => saved = w), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "BUY" });

        Assert.NotNull(saved);
        Assert.Equal(WatchStatus.Approved, saved.Status);
    }

    [Fact]
    public async Task Callback_SkipDecision_UpdatesWatchStatusToActive()
    {
        var (approval, watch) = BuildPendingApproval();
        SetupApprovalMocks(approval, watch);

        WatchRequest? saved = null;
        _factory.WatchRepository
            .UpdateAsync(Arg.Do<WatchRequest>(w => saved = w), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "SKIP" });

        Assert.NotNull(saved);
        Assert.Equal(WatchStatus.Active, saved.Status);
    }

    [Fact]
    public async Task Callback_BuyDecision_PublishesApprovalDecidedEvent()
    {
        var (approval, watch) = BuildPendingApproval();
        SetupApprovalMocks(approval, watch);

        await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "BUY" });

        await _factory.EventPublisher.Received(1)
            .PublishAsync(
                Arg.Is<ApprovalDecided>(e =>
                    e.Decision == ApprovalDecision.Approved &&
                    e.ApprovalId == approval.Id),
                TopicNames.ApprovalDecided,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callback_SkipDecision_PublishesApprovalDecidedEvent()
    {
        var (approval, watch) = BuildPendingApproval();
        SetupApprovalMocks(approval, watch);

        await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "SKIP" });

        await _factory.EventPublisher.Received(1)
            .PublishAsync(
                Arg.Is<ApprovalDecided>(e =>
                    e.Decision == ApprovalDecision.Rejected &&
                    e.ApprovalId == approval.Id),
                TopicNames.ApprovalDecided,
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Callback_MissingToken_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = "", decision = "BUY" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Callback_MissingDecision_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = "some-token", decision = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Callback_TokenNotFound_Returns404()
    {
        _factory.ApprovalRepository
            .GetByTokenAsync("no-such-token", Arg.Any<CancellationToken>())
            .Returns((ApprovalRecord?)null);

        var response = await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = "no-such-token", decision = "BUY" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Callback_AlreadyResolvedApproval_Returns400()
    {
        var (approval, _) = BuildPendingApproval();
        approval.Decision = ApprovalDecision.Approved; // already resolved

        _factory.ApprovalRepository
            .GetByTokenAsync(approval.ApprovalToken, Arg.Any<CancellationToken>())
            .Returns(approval);

        var response = await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "BUY" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Approved", body);
    }

    [Fact]
    public async Task Callback_ExpiredToken_Returns400()
    {
        var (approval, _) = BuildPendingApproval();
        approval.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); // expired

        _factory.ApprovalRepository
            .GetByTokenAsync(approval.ApprovalToken, Arg.Any<CancellationToken>())
            .Returns(approval);

        var response = await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "BUY" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("expired", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Callback_InvalidDecisionString_Returns400()
    {
        var (approval, _) = BuildPendingApproval();

        _factory.ApprovalRepository
            .GetByTokenAsync(approval.ApprovalToken, Arg.Any<CancellationToken>())
            .Returns(approval);

        var response = await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "MAYBE" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("MAYBE", body);
    }

    [Fact]
    public async Task Callback_IsCaseInsensitive_AcceptsLowercaseBuy()
    {
        var (approval, watch) = BuildPendingApproval();
        SetupApprovalMocks(approval, watch);

        var response = await _client.PostAsJsonAsync("/api/a2p/callback",
            new { token = approval.ApprovalToken, decision = "buy" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (ApprovalRecord, WatchRequest) BuildPendingApproval()
    {
        var watch = new WatchRequest
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            ProductName = "Test Product",
            MaxPrice = 500m,
            Status = WatchStatus.AwaitingApproval,
            StatusHistory =
            [
                new(WatchStatus.Active, WatchStatus.Matched, DateTimeOffset.UtcNow.AddMinutes(-30), null),
                new(WatchStatus.Matched, WatchStatus.AwaitingApproval, DateTimeOffset.UtcNow.AddMinutes(-20), null)
            ]
        };

        var approval = new ApprovalRecord
        {
            Id = Guid.NewGuid(),
            MatchId = Guid.NewGuid(),
            WatchRequestId = watch.Id,
            UserId = watch.UserId,
            ApprovalToken = Guid.NewGuid().ToString("N"),
            SentAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            Decision = ApprovalDecision.Pending,
            Channel = NotificationChannel.A2P_SMS
        };

        return (approval, watch);
    }

    private void SetupApprovalMocks(ApprovalRecord approval, WatchRequest watch)
    {
        _factory.ApprovalRepository
            .GetByTokenAsync(approval.ApprovalToken, Arg.Any<CancellationToken>())
            .Returns(approval);

        _factory.ApprovalRepository
            .UpdateAsync(Arg.Any<ApprovalRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _factory.WatchRepository
            .GetByIdAsync(watch.Id, watch.UserId, Arg.Any<CancellationToken>())
            .Returns(watch);

        _factory.WatchRepository
            .UpdateAsync(Arg.Any<WatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _factory.EventPublisher
            .PublishAsync(Arg.Any<ApprovalDecided>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }
}

// ── Response DTO for deserialization ─────────────────────────────────────────

file sealed record CallbackResponseDto(
    Guid ApprovalId,
    string Decision,
    Guid WatchRequestId,
    DateTimeOffset RespondedAt
);
