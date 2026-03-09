using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;

namespace AgentPayWatch.Agents.Approval.Tests;

// ── FakeApprovalRepository ────────────────────────────────────────────────────

internal sealed class FakeApprovalRepository : IApprovalRepository
{
    private readonly Dictionary<Guid, ApprovalRecord> _store = [];

    public List<ApprovalRecord> CreatedApprovals { get; } = [];
    public List<ApprovalRecord> UpdatedApprovals { get; } = [];

    public FakeApprovalRepository(params ApprovalRecord[] seed)
    {
        foreach (var a in seed)
            _store[a.Id] = a;
    }

    public Task<ApprovalRecord> CreateAsync(ApprovalRecord approval, CancellationToken ct = default)
    {
        _store[approval.Id] = approval;
        CreatedApprovals.Add(approval);
        return Task.FromResult(approval);
    }

    public Task<ApprovalRecord?> GetByIdAsync(Guid id, Guid watchRequestId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(id));

    public Task<ApprovalRecord?> GetByTokenAsync(string token, CancellationToken ct = default) =>
        Task.FromResult(_store.Values.FirstOrDefault(a => a.ApprovalToken == token));

    public Task<ApprovalRecord?> GetByMatchIdAsync(Guid matchId, Guid watchRequestId, CancellationToken ct = default) =>
        Task.FromResult(_store.Values.FirstOrDefault(a => a.MatchId == matchId && a.WatchRequestId == watchRequestId));

    public Task UpdateAsync(ApprovalRecord approval, CancellationToken ct = default)
    {
        _store[approval.Id] = approval;
        UpdatedApprovals.Add(approval);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ApprovalRecord>> GetPendingExpiredAsync(DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApprovalRecord>>(
            _store.Values
                .Where(a => a.Decision == ApprovalDecision.Pending && a.ExpiresAt < now)
                .ToList());
}

// ── FakeWatchRequestRepository ────────────────────────────────────────────────

internal sealed class FakeWatchRequestRepository : IWatchRequestRepository
{
    private readonly Dictionary<Guid, WatchRequest> _store;

    public FakeWatchRequestRepository(params WatchRequest[] watches)
    {
        _store = watches.ToDictionary(w => w.Id);
    }

    public Task<WatchRequest> CreateAsync(WatchRequest watchRequest, CancellationToken ct = default)
    {
        _store[watchRequest.Id] = watchRequest;
        return Task.FromResult(watchRequest);
    }

    public Task<WatchRequest?> GetByIdAsync(Guid id, string? userId = null, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(id));

    public Task<IReadOnlyList<WatchRequest>> GetByUserIdAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WatchRequest>>(
            _store.Values.Where(w => w.UserId == userId).ToList());

    public Task<IReadOnlyList<WatchRequest>> GetByStatusAsync(WatchStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WatchRequest>>(
            _store.Values.Where(w => w.Status == status).ToList());

    public Task UpdateAsync(WatchRequest watchRequest, CancellationToken ct = default)
    {
        _store[watchRequest.Id] = watchRequest;
        return Task.CompletedTask;
    }

    public WatchRequest? GetById(Guid id) => _store.GetValueOrDefault(id);
}

// ── FakeEventPublisher ────────────────────────────────────────────────────────

internal sealed class FakeEventPublisher : IEventPublisher
{
    public List<(object Event, string Topic)> PublishedEvents { get; } = [];

    public Task PublishAsync<T>(T message, string topicName, CancellationToken ct)
    {
        PublishedEvents.Add((message!, topicName));
        return Task.CompletedTask;
    }
}
