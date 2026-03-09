using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Domain.Models;

namespace AgentPayWatch.Agents.ProductWatch.Tests;

// ── FakeWatchRequestRepository ────────────────────────────────────────────────

internal sealed class FakeWatchRequestRepository : IWatchRequestRepository
{
    private readonly Dictionary<Guid, WatchRequest> _store;

    public FakeWatchRequestRepository(params WatchRequest[] watches)
    {
        _store = watches.ToDictionary(w => w.Id);
    }

    public WatchRequest? GetById(Guid id) =>
        _store.GetValueOrDefault(id);

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
}

// ── FakeProductMatchRepository ────────────────────────────────────────────────

internal sealed class FakeProductMatchRepository : IProductMatchRepository
{
    public List<ProductMatch> CreatedMatches { get; } = [];

    public Task<ProductMatch> CreateAsync(ProductMatch match, CancellationToken ct = default)
    {
        CreatedMatches.Add(match);
        return Task.FromResult(match);
    }

    public Task<ProductMatch?> GetByIdAsync(Guid id, Guid watchRequestId, CancellationToken ct = default) =>
        Task.FromResult(CreatedMatches.FirstOrDefault(m => m.Id == id && m.WatchRequestId == watchRequestId));

    public Task<IReadOnlyList<ProductMatch>> GetByWatchRequestIdAsync(Guid watchRequestId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductMatch>>(
            CreatedMatches.Where(m => m.WatchRequestId == watchRequestId).ToList());
}

// ── FakeProductSource ─────────────────────────────────────────────────────────

internal sealed class FakeProductSource : IProductSource
{
    private readonly IReadOnlyList<ProductListing> _listings;

    public FakeProductSource(IReadOnlyList<ProductListing> listings)
    {
        _listings = listings;
    }

    public Task<IReadOnlyList<ProductListing>> SearchAsync(string productName, CancellationToken ct) =>
        Task.FromResult(_listings);
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
