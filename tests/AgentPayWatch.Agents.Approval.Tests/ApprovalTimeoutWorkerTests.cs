using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPayWatch.Agents.Approval.Tests;

public sealed class ApprovalTimeoutWorkerTests
{
    // ── Test 1: No expired approvals → nothing happens ────────────────────────

    [Fact]
    public async Task ExecuteAsync_DoesNothing_WhenNoExpiredApprovals()
    {
        var approval = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(10));
        var (_, watchRepo, publisher) = await RunOneCycleAsync(
            approvals: [approval],
            watches: [MakeAwaitingWatch(approval.WatchRequestId, approval.UserId)]);

        Assert.Empty(publisher.PublishedEvents);
    }

    // ── Test 2: Expired approval → decision updated to Expired ────────────────

    [Fact]
    public async Task ExecuteAsync_SetsDecisionExpired_WhenApprovalExpired()
    {
        var approval = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var watch = MakeAwaitingWatch(approval.WatchRequestId, approval.UserId);
        var (approvalRepo, _, _) = await RunOneCycleAsync([approval], [watch]);

        var updated = approvalRepo.UpdatedApprovals.FirstOrDefault(a => a.Id == approval.Id);
        Assert.NotNull(updated);
        Assert.Equal(ApprovalDecision.Expired, updated.Decision);
        Assert.NotNull(updated.RespondedAt);
    }

    // ── Test 3: Expired approval → watch reactivated to Active ───────────────

    [Fact]
    public async Task ExecuteAsync_ReactivatesWatch_WhenApprovalExpired()
    {
        var approval = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var watch = MakeAwaitingWatch(approval.WatchRequestId, approval.UserId);
        var (_, watchRepo, _) = await RunOneCycleAsync([approval], [watch]);

        var updated = watchRepo.GetById(watch.Id);
        Assert.NotNull(updated);
        Assert.Equal(WatchStatus.Active, updated.Status);
    }

    // ── Test 4: Expired approval → ApprovalDecided event published ───────────

    [Fact]
    public async Task ExecuteAsync_PublishesApprovalDecidedEvent_WhenApprovalExpired()
    {
        var approval = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var watch = MakeAwaitingWatch(approval.WatchRequestId, approval.UserId);
        var (_, _, publisher) = await RunOneCycleAsync([approval], [watch]);

        Assert.Single(publisher.PublishedEvents);
        var (evt, topic) = publisher.PublishedEvents[0];
        Assert.IsType<ApprovalDecided>(evt);
        Assert.Equal(TopicNames.ApprovalDecided, topic);

        var decided = (ApprovalDecided)evt;
        Assert.Equal(ApprovalDecision.Expired, decided.Decision);
        Assert.Equal(approval.Id, decided.ApprovalId);
        Assert.Equal(approval.MatchId, decided.MatchId);
        Assert.Equal(approval.WatchRequestId, decided.CorrelationId);
        Assert.Equal("approval-agent", decided.Source);
    }

    // ── Test 5: Multiple expired approvals → all processed ───────────────────

    [Fact]
    public async Task ExecuteAsync_ProcessesAllExpiredApprovals()
    {
        var approval1 = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var approval2 = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var watch1 = MakeAwaitingWatch(approval1.WatchRequestId, approval1.UserId);
        var watch2 = MakeAwaitingWatch(approval2.WatchRequestId, approval2.UserId);

        var (approvalRepo, _, publisher) = await RunOneCycleAsync(
            [approval1, approval2],
            [watch1, watch2]);

        Assert.Equal(2, approvalRepo.UpdatedApprovals.Count(a => a.Decision == ApprovalDecision.Expired));
        Assert.Equal(2, publisher.PublishedEvents.Count);
    }

    // ── Test 6: Expired approval but watch missing → event still published ────

    [Fact]
    public async Task ExecuteAsync_StillPublishesEvent_WhenWatchNotFound()
    {
        var approval = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        // No watch seeded — simulate watch already deleted
        var (_, _, publisher) = await RunOneCycleAsync([approval], []);

        Assert.Single(publisher.PublishedEvents);
        var (evt, _) = publisher.PublishedEvents[0];
        Assert.IsType<ApprovalDecided>(evt);
        Assert.Equal(ApprovalDecision.Expired, ((ApprovalDecided)evt).Decision);
    }

    // ── Test 7: Still-pending (not expired) approval is not touched ───────────

    [Fact]
    public async Task ExecuteAsync_Ignores_NonExpiredPendingApprovals()
    {
        var expired = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var notExpired = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(10));
        var watch = MakeAwaitingWatch(expired.WatchRequestId, expired.UserId);

        var (approvalRepo, _, publisher) = await RunOneCycleAsync(
            [expired, notExpired],
            [watch]);

        Assert.Single(publisher.PublishedEvents);
        Assert.DoesNotContain(approvalRepo.UpdatedApprovals, a => a.Id == notExpired.Id);
    }

    // ── Test 8: Already-resolved approval is not re-processed ─────────────────

    [Fact]
    public async Task ExecuteAsync_Ignores_AlreadyResolvedApprovals()
    {
        var resolved = MakePendingApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        resolved.Decision = ApprovalDecision.Approved; // already resolved

        var (_, _, publisher) = await RunOneCycleAsync([resolved], []);

        Assert.Empty(publisher.PublishedEvents);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(FakeApprovalRepository, FakeWatchRequestRepository, FakeEventPublisher)> RunOneCycleAsync(
        ApprovalRecord[] approvals,
        WatchRequest[] watches)
    {
        var approvalRepo = new FakeApprovalRepository(approvals);
        var watchRepo = new FakeWatchRequestRepository(watches);
        var publisher = new FakeEventPublisher();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Approval:TimeoutCheckIntervalSeconds"] = "60"
            })
            .Build();

        var worker = new ApprovalTimeoutWorker(
            approvalRepo,
            watchRepo,
            publisher,
            NullLogger<ApprovalTimeoutWorker>.Instance,
            config);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(300);
        await worker.StopAsync(CancellationToken.None);

        return (approvalRepo, watchRepo, publisher);
    }

    private static ApprovalRecord MakePendingApproval(DateTimeOffset expiresAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            MatchId = Guid.NewGuid(),
            WatchRequestId = Guid.NewGuid(),
            UserId = "user-1",
            ApprovalToken = Guid.NewGuid().ToString("N"),
            SentAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            ExpiresAt = expiresAt,
            Decision = ApprovalDecision.Pending,
            Channel = NotificationChannel.A2P_SMS
        };

    private static WatchRequest MakeAwaitingWatch(Guid id, string userId) =>
        new()
        {
            Id = id,
            UserId = userId,
            ProductName = "Test Product",
            MaxPrice = 500m,
            Status = WatchStatus.AwaitingApproval,
            StatusHistory =
            [
                new(WatchStatus.Active, WatchStatus.Matched, DateTimeOffset.UtcNow.AddMinutes(-30), null),
                new(WatchStatus.Matched, WatchStatus.AwaitingApproval, DateTimeOffset.UtcNow.AddMinutes(-20), null)
            ]
        };
}
