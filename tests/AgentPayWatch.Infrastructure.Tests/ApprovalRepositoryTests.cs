using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Infrastructure.Cosmos;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace AgentPayWatch.Infrastructure.Tests;

[Collection("CosmosIntegration")]
public sealed class ApprovalRepositoryTests : IAsyncLifetime
{
    private readonly CosmosFixture _fixture;
    private CosmosApprovalRepository _repo = null!;

    public ApprovalRepositoryTests(CosmosFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        Skip.If(!_fixture.IsAvailable,
            $"Cosmos DB emulator unreachable — start Aspire first. Reason: {_fixture.UnavailableReason}");
        _repo = new CosmosApprovalRepository(_fixture.Client);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_fixture.IsAvailable)
            await _fixture.DeleteAllApprovalDocumentsAsync();
    }

    // ── CreateAsync ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateAsync_StoresDocumentInApprovalsContainer()
    {
        var approval = MakeApproval();

        await _repo.CreateAsync(approval);

        // Direct Cosmos read to verify partition key mapping and field serialization.
        var response = await _fixture.ApprovalsContainer.ReadItemAsync<dynamic>(
            approval.Id.ToString(), new PartitionKey(approval.WatchRequestId.ToString()));
        dynamic doc = response.Resource;

        Assert.Equal(approval.Id.ToString(), (string)doc.id);
        Assert.Equal(approval.WatchRequestId.ToString(), (string)doc.watchRequestId);
        Assert.Equal(approval.MatchId.ToString(), (string)doc.matchId);
        Assert.Equal(approval.UserId, (string)doc.userId);
        Assert.Equal(approval.ApprovalToken, (string)doc.approvalToken);
        Assert.Equal("pending", (string)doc.decision);          // enum → camelCase string
        Assert.NotNull((string)doc.channel);                    // channel field is present
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetByIdAsync_ReturnsStoredApproval()
    {
        var approval = MakeApproval();
        await _repo.CreateAsync(approval);

        var fetched = await _repo.GetByIdAsync(approval.Id, approval.WatchRequestId);

        Assert.NotNull(fetched);
        Assert.Equal(approval.Id, fetched.Id);
        Assert.Equal(approval.WatchRequestId, fetched.WatchRequestId);
        Assert.Equal(approval.MatchId, fetched.MatchId);
        Assert.Equal(approval.UserId, fetched.UserId);
        Assert.Equal(approval.ApprovalToken, fetched.ApprovalToken);
        Assert.Equal(ApprovalDecision.Pending, fetched.Decision);
        Assert.Equal(NotificationChannel.A2P_SMS, fetched.Channel);
        Assert.Null(fetched.RespondedAt);
    }

    [SkippableFact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(result);
    }

    // ── GetByTokenAsync (cross-partition query) ───────────────────────────────

    [SkippableFact]
    public async Task GetByTokenAsync_ReturnsApproval_WhenTokenExists()
    {
        var approval = MakeApproval(token: "tok-abc-123");
        await _repo.CreateAsync(approval);

        var fetched = await _repo.GetByTokenAsync("tok-abc-123");

        Assert.NotNull(fetched);
        Assert.Equal(approval.Id, fetched.Id);
        Assert.Equal("tok-abc-123", fetched.ApprovalToken);
    }

    [SkippableFact]
    public async Task GetByTokenAsync_ReturnsNull_WhenTokenNotFound()
    {
        var result = await _repo.GetByTokenAsync("nonexistent-token");
        Assert.Null(result);
    }

    [SkippableFact]
    public async Task GetByTokenAsync_FindsApproval_AcrossMultiplePartitions()
    {
        // Approvals stored in different watch-request partitions.
        var approval1 = MakeApproval(token: "token-watch-1");
        var approval2 = MakeApproval(token: "token-watch-2"); // different WatchRequestId → different partition
        await _repo.CreateAsync(approval1);
        await _repo.CreateAsync(approval2);

        var found1 = await _repo.GetByTokenAsync("token-watch-1");
        var found2 = await _repo.GetByTokenAsync("token-watch-2");

        Assert.NotNull(found1);
        Assert.NotNull(found2);
        Assert.Equal(approval1.Id, found1.Id);
        Assert.Equal(approval2.Id, found2.Id);
    }

    // ── GetByMatchIdAsync ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetByMatchIdAsync_ReturnsApproval_WhenMatchExists()
    {
        var approval = MakeApproval();
        await _repo.CreateAsync(approval);

        var fetched = await _repo.GetByMatchIdAsync(approval.MatchId, approval.WatchRequestId);

        Assert.NotNull(fetched);
        Assert.Equal(approval.Id, fetched.Id);
        Assert.Equal(approval.MatchId, fetched.MatchId);
    }

    [SkippableFact]
    public async Task GetByMatchIdAsync_ReturnsNull_WhenMatchNotFound()
    {
        var result = await _repo.GetByMatchIdAsync(Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(result);
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task UpdateAsync_PersistsDecisionChange()
    {
        var approval = MakeApproval();
        await _repo.CreateAsync(approval);

        approval.Decision = ApprovalDecision.Approved;
        approval.RespondedAt = DateTimeOffset.UtcNow;
        await _repo.UpdateAsync(approval);

        var fetched = await _repo.GetByIdAsync(approval.Id, approval.WatchRequestId);

        Assert.NotNull(fetched);
        Assert.Equal(ApprovalDecision.Approved, fetched.Decision);
        Assert.NotNull(fetched.RespondedAt);
    }

    [SkippableFact]
    public async Task UpdateAsync_PersistsRejectedDecision()
    {
        var approval = MakeApproval();
        await _repo.CreateAsync(approval);

        approval.Decision = ApprovalDecision.Rejected;
        approval.RespondedAt = DateTimeOffset.UtcNow;
        await _repo.UpdateAsync(approval);

        var fetched = await _repo.GetByIdAsync(approval.Id, approval.WatchRequestId);

        Assert.NotNull(fetched);
        Assert.Equal(ApprovalDecision.Rejected, fetched.Decision);
    }

    // ── GetPendingExpiredAsync (cross-partition query) ────────────────────────

    [SkippableFact]
    public async Task GetPendingExpiredAsync_ReturnsExpiredPendingApprovals()
    {
        var expired = MakeApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var notExpired = MakeApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(15));
        var alreadyResolved = MakeApproval(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        alreadyResolved.Decision = ApprovalDecision.Approved;

        await _repo.CreateAsync(expired);
        await _repo.CreateAsync(notExpired);
        await _repo.CreateAsync(alreadyResolved);
        await _repo.UpdateAsync(alreadyResolved);

        var results = await _repo.GetPendingExpiredAsync(DateTimeOffset.UtcNow);

        // Only the one that is Pending AND past ExpiresAt should come back.
        Assert.Single(results, r => r.Id == expired.Id);
        Assert.DoesNotContain(results, r => r.Id == notExpired.Id);
        Assert.DoesNotContain(results, r => r.Id == alreadyResolved.Id);
    }

    [SkippableFact]
    public async Task GetPendingExpiredAsync_ReturnsEmpty_WhenNoneExpired()
    {
        var active = MakeApproval(expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        await _repo.CreateAsync(active);

        var results = await _repo.GetPendingExpiredAsync(DateTimeOffset.UtcNow);

        Assert.DoesNotContain(results, r => r.Id == active.Id);
    }

    // ── GetByTokenAsync after Update ──────────────────────────────────────────

    [SkippableFact]
    public async Task GetByTokenAsync_ReflectsUpdatedDecision()
    {
        var approval = MakeApproval(token: "tok-update-test");
        await _repo.CreateAsync(approval);

        approval.Decision = ApprovalDecision.Approved;
        approval.RespondedAt = DateTimeOffset.UtcNow;
        await _repo.UpdateAsync(approval);

        var fetched = await _repo.GetByTokenAsync("tok-update-test");

        Assert.NotNull(fetched);
        Assert.Equal(ApprovalDecision.Approved, fetched.Decision);
        Assert.NotNull(fetched.RespondedAt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ApprovalRecord MakeApproval(
        string? token = null,
        DateTimeOffset? expiresAt = null) => new()
    {
        Id = Guid.NewGuid(),
        MatchId = Guid.NewGuid(),
        WatchRequestId = Guid.NewGuid(),
        UserId = "user-approval-tests",
        ApprovalToken = token ?? $"tok-{Guid.NewGuid():N}",
        SentAt = DateTimeOffset.UtcNow,
        ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15),
        RespondedAt = null,
        Decision = ApprovalDecision.Pending,
        Channel = NotificationChannel.A2P_SMS
    };
}
