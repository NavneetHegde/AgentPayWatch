using System.Collections.Concurrent;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Domain.Models;

namespace AgentPayWatch.E2ETests;

// ── InMemoryWatchRequestRepository ───────────────────────────────────────────

public sealed class InMemoryWatchRequestRepository : IWatchRequestRepository
{
    private readonly ConcurrentDictionary<Guid, WatchRequest> _store = new();

    public Task<WatchRequest> CreateAsync(WatchRequest watch, CancellationToken ct = default)
    {
        _store[watch.Id] = watch;
        return Task.FromResult(watch);
    }

    public Task<WatchRequest?> GetByIdAsync(Guid id, string? userId = null, CancellationToken ct = default) =>
        Task.FromResult(_store.GetValueOrDefault(id));

    public Task<IReadOnlyList<WatchRequest>> GetByUserIdAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WatchRequest>>(
            _store.Values.Where(w => w.UserId == userId).ToList());

    public Task<IReadOnlyList<WatchRequest>> GetByStatusAsync(WatchStatus status, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<WatchRequest>>(
            _store.Values.Where(w => w.Status == status).ToList());

    public Task UpdateAsync(WatchRequest watch, CancellationToken ct = default)
    {
        _store[watch.Id] = watch;
        return Task.CompletedTask;
    }
}

// ── InMemoryProductMatchRepository ───────────────────────────────────────────

public sealed class InMemoryProductMatchRepository : IProductMatchRepository
{
    private readonly ConcurrentDictionary<Guid, ProductMatch> _store = new();

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

// ── InMemoryApprovalRepository ────────────────────────────────────────────────

public sealed class InMemoryApprovalRepository : IApprovalRepository
{
    private readonly ConcurrentDictionary<Guid, ApprovalRecord> _store = new();

    public Task<ApprovalRecord> CreateAsync(ApprovalRecord approval, CancellationToken ct = default)
    {
        _store[approval.Id] = approval;
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
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ApprovalRecord>> GetPendingExpiredAsync(DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApprovalRecord>>(
            _store.Values
                .Where(a => a.Decision == ApprovalDecision.Pending && a.ExpiresAt < now)
                .ToList());
}

// ── InMemoryPaymentTransactionRepository ─────────────────────────────────────

public sealed class InMemoryPaymentTransactionRepository : IPaymentTransactionRepository
{
    private readonly ConcurrentDictionary<Guid, PaymentTransaction> _store = new();

    public List<PaymentTransaction> All => _store.Values.ToList();

    public Task<PaymentTransaction> CreateAsync(PaymentTransaction transaction, CancellationToken ct = default)
    {
        _store[transaction.Id] = transaction;
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

// ── InMemoryEventPublisher ────────────────────────────────────────────────────

public sealed class InMemoryEventPublisher : IEventPublisher
{
    private readonly List<(object Event, string Topic)> _events = [];
    private readonly object _lock = new();

    public IReadOnlyList<(object Event, string Topic)> PublishedEvents
    {
        get { lock (_lock) { return _events.ToList(); } }
    }

    public Task PublishAsync<T>(T message, string topicName, CancellationToken ct)
    {
        lock (_lock) { _events.Add((message!, topicName)); }
        return Task.CompletedTask;
    }

    public IEnumerable<T> EventsOfType<T>() =>
        PublishedEvents.Where(e => e.Event is T).Select(e => (T)e.Event);
}

// ── ConfigurableProductSource ─────────────────────────────────────────────────

public sealed class ConfigurableProductSource : IProductSource
{
    private readonly List<ProductListing> _listings = [];

    public void SetListings(params ProductListing[] listings)
    {
        _listings.Clear();
        _listings.AddRange(listings);
    }

    public void Clear() => _listings.Clear();

    public Task<IReadOnlyList<ProductListing>> SearchAsync(string productName, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProductListing>>(
            _listings
                .Where(l => l.Name.Contains(productName, StringComparison.OrdinalIgnoreCase))
                .ToList());
}

// ── FakeA2PClient ─────────────────────────────────────────────────────────────

public sealed class FakeA2PClient : IA2PClient
{
    public record SentMessage(
        string PhoneNumber,
        string ProductName,
        decimal Price,
        string Seller,
        string ApprovalToken);

    private readonly List<SentMessage> _messages = [];
    private readonly object _lock = new();

    public IReadOnlyList<SentMessage> SentMessages
    {
        get { lock (_lock) { return _messages.ToList(); } }
    }

    public string? LastApprovalToken
    {
        get { lock (_lock) { return _messages.LastOrDefault()?.ApprovalToken; } }
    }

    public Task<bool> SendApprovalRequestAsync(
        string phoneNumber,
        string productName,
        decimal price,
        string seller,
        string approvalToken,
        CancellationToken ct)
    {
        lock (_lock)
        {
            _messages.Add(new(phoneNumber, productName, price, seller, approvalToken));
        }
        return Task.FromResult(true);
    }
}

// ── ConfigurablePaymentProvider ───────────────────────────────────────────────

public sealed class ConfigurablePaymentProvider : IPaymentProvider
{
    private bool _shouldSucceed = true;
    private string _failureReason = "Card declined";

    public void SetSuccess() => _shouldSucceed = true;

    public void SetFailure(string reason = "Card declined")
    {
        _shouldSucceed = false;
        _failureReason = reason;
    }

    public Task<PaymentResult> ExecutePaymentAsync(
        string idempotencyKey,
        decimal amount,
        string currency,
        string merchant,
        string paymentMethodToken,
        CancellationToken ct)
    {
        var result = _shouldSucceed
            ? new PaymentResult(true, $"PAY-{Guid.NewGuid():N}", null)
            : new PaymentResult(false, null, _failureReason);

        return Task.FromResult(result);
    }
}
