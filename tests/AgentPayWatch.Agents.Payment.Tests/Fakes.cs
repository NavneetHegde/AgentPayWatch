using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;

namespace AgentPayWatch.Agents.Payment.Tests;

// ── FakeWatchRequestRepository ────────────────────────────────────────────────

internal sealed class FakeWatchRequestRepository : IWatchRequestRepository
{
    private readonly Dictionary<Guid, WatchRequest> _store;

    public List<WatchRequest> UpdatedWatches { get; } = [];

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
        UpdatedWatches.Add(watchRequest);
        return Task.CompletedTask;
    }

    public WatchRequest? GetById(Guid id) => _store.GetValueOrDefault(id);
}

// ── FakeProductMatchRepository ────────────────────────────────────────────────

internal sealed class FakeProductMatchRepository : IProductMatchRepository
{
    private readonly Dictionary<Guid, ProductMatch> _store;

    public FakeProductMatchRepository(params ProductMatch[] matches)
    {
        _store = matches.ToDictionary(m => m.Id);
    }

    public Task<ProductMatch> CreateAsync(ProductMatch match, CancellationToken ct = default)
    {
        _store[match.Id] = match;
        return Task.FromResult(match);
    }

    public Task<ProductMatch?> GetByIdAsync(Guid id, Guid watchRequestId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(id));

    public Task<IReadOnlyList<ProductMatch>> GetByWatchRequestIdAsync(Guid watchRequestId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProductMatch>>(
            _store.Values.Where(m => m.WatchRequestId == watchRequestId).ToList());
}

// ── FakePaymentTransactionRepository ─────────────────────────────────────────

internal sealed class FakePaymentTransactionRepository : IPaymentTransactionRepository
{
    private readonly Dictionary<Guid, PaymentTransaction> _store = [];

    public List<PaymentTransaction> CreatedTransactions { get; } = [];

    public Task<PaymentTransaction> CreateAsync(PaymentTransaction transaction, CancellationToken ct = default)
    {
        _store[transaction.Id] = transaction;
        CreatedTransactions.Add(transaction);
        return Task.FromResult(transaction);
    }

    public Task<PaymentTransaction?> GetByIdAsync(Guid id, string userId, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(id));

    public Task<IReadOnlyList<PaymentTransaction>> GetByUserIdAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PaymentTransaction>>(
            _store.Values.Where(t => t.UserId == userId).ToList());

    public Task UpdateAsync(PaymentTransaction transaction, CancellationToken ct = default)
    {
        _store[transaction.Id] = transaction;
        return Task.CompletedTask;
    }
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

// ── FakePaymentProvider ───────────────────────────────────────────────────────

internal sealed class FakePaymentProvider : IPaymentProvider
{
    private readonly bool _alwaysSucceed;
    private readonly string _failureReason;

    public List<(string Key, decimal Amount, string Currency, string Merchant, string Token)> Calls { get; } = [];

    public FakePaymentProvider(bool alwaysSucceed = true, string failureReason = "Card declined")
    {
        _alwaysSucceed = alwaysSucceed;
        _failureReason = failureReason;
    }

    public Task<PaymentResult> ExecutePaymentAsync(
        string idempotencyKey,
        decimal amount,
        string currency,
        string merchant,
        string paymentMethodToken,
        CancellationToken ct)
    {
        Calls.Add((idempotencyKey, amount, currency, merchant, paymentMethodToken));

        var result = _alwaysSucceed
            ? new PaymentResult(true, $"PAY-{Guid.NewGuid():N}", null)
            : new PaymentResult(false, null, _failureReason);

        return Task.FromResult(result);
    }
}
