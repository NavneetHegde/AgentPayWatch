extern alias PaymentAgent;
extern alias ProductWatchAgent;

using System.Security.Cryptography;
using PaymentAgent::AgentPayWatch.Agents.Payment;
using ProductWatchAgent::AgentPayWatch.Agents.ProductWatch;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentPayWatch.E2ETests;

/// <summary>
/// Simulates the agent pipeline directly (without Service Bus) using shared in-memory
/// repositories. Each method mirrors the core logic of the corresponding BackgroundService.
/// </summary>
public sealed class AgentSimulator
{
    private readonly InMemoryWatchRequestRepository _watchRepo;
    private readonly InMemoryProductMatchRepository _matchRepo;
    private readonly InMemoryApprovalRepository _approvalRepo;
    private readonly InMemoryPaymentTransactionRepository _transactionRepo;
    private readonly InMemoryEventPublisher _eventPublisher;
    private readonly ConfigurableProductSource _productSource;
    private readonly FakeA2PClient _a2pClient;
    private readonly ConfigurablePaymentProvider _paymentProvider;

    public AgentSimulator(
        InMemoryWatchRequestRepository watchRepo,
        InMemoryProductMatchRepository matchRepo,
        InMemoryApprovalRepository approvalRepo,
        InMemoryPaymentTransactionRepository transactionRepo,
        InMemoryEventPublisher eventPublisher,
        ConfigurableProductSource productSource,
        FakeA2PClient a2pClient,
        ConfigurablePaymentProvider paymentProvider)
    {
        _watchRepo = watchRepo;
        _matchRepo = matchRepo;
        _approvalRepo = approvalRepo;
        _transactionRepo = transactionRepo;
        _eventPublisher = eventPublisher;
        _productSource = productSource;
        _a2pClient = a2pClient;
        _paymentProvider = paymentProvider;
    }

    /// <summary>
    /// Mimics ProductWatchWorker: scans active watches against the product source
    /// and records any matches. Watches with a match transition to <c>Matched</c>.
    /// </summary>
    public async Task RunProductWatchScanAsync(CancellationToken ct = default)
    {
        var activeWatches = await _watchRepo.GetByStatusAsync(WatchStatus.Active, ct);

        foreach (var watch in activeWatches)
        {
            var listings = await _productSource.SearchAsync(watch.ProductName, ct);

            var bestMatch = listings
                .Where(l => MatchingService.IsMatch(watch, l))
                .OrderBy(l => l.Price)
                .FirstOrDefault();

            if (bestMatch is null)
                continue;

            var match = new ProductMatch
            {
                Id = Guid.NewGuid(),
                WatchRequestId = watch.Id,
                UserId = watch.UserId,
                ProductName = bestMatch.Name,
                Price = bestMatch.Price,
                Currency = bestMatch.Currency,
                Seller = bestMatch.Seller,
                ProductUrl = bestMatch.Url,
                MatchedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
                Availability = bestMatch.Availability
            };

            await _matchRepo.CreateAsync(match, ct);

            watch.UpdateStatus(WatchStatus.Matched);
            await _watchRepo.UpdateAsync(watch, ct);

            await _eventPublisher.PublishAsync(
                new ProductMatchFound(
                    MessageId: Guid.NewGuid(),
                    CorrelationId: watch.Id,
                    Timestamp: DateTimeOffset.UtcNow,
                    Source: "product-watch-agent",
                    MatchId: match.Id,
                    ProductName: match.ProductName,
                    Price: match.Price,
                    Currency: match.Currency,
                    Seller: match.Seller),
                TopicNames.ProductMatchFound,
                ct);
        }
    }

    /// <summary>
    /// Mimics ApprovalWorker: for every watch in <c>Matched</c> status, creates an
    /// ApprovalRecord, transitions the watch to <c>AwaitingApproval</c>, and sends
    /// an A2P approval message.
    /// </summary>
    public async Task RunApprovalProcessingAsync(CancellationToken ct = default)
    {
        var matchedWatches = await _watchRepo.GetByStatusAsync(WatchStatus.Matched, ct);

        foreach (var watch in matchedWatches)
        {
            var matches = await _matchRepo.GetByWatchRequestIdAsync(watch.Id, ct);
            var match = matches.FirstOrDefault();
            if (match is null)
                continue;

            var token = GenerateToken();
            var now = DateTimeOffset.UtcNow;

            var approval = new ApprovalRecord
            {
                Id = Guid.NewGuid(),
                MatchId = match.Id,
                WatchRequestId = watch.Id,
                UserId = watch.UserId,
                ApprovalToken = token,
                SentAt = now,
                ExpiresAt = now.AddMinutes(15),
                RespondedAt = null,
                Decision = ApprovalDecision.Pending,
                Channel = watch.NotificationChannel
            };

            await _approvalRepo.CreateAsync(approval, ct);

            watch.UpdateStatus(WatchStatus.AwaitingApproval);
            await _watchRepo.UpdateAsync(watch, ct);

            await _a2pClient.SendApprovalRequestAsync(
                watch.PhoneNumber,
                match.ProductName,
                match.Price,
                match.Seller,
                token,
                ct);
        }
    }

    /// <summary>
    /// Mimics PaymentWorker: picks up the latest <see cref="ApprovalDecided"/> event
    /// published with Decision=Approved and runs the payment flow.
    /// </summary>
    public async Task RunPaymentAsync(CancellationToken ct = default)
    {
        var approvalDecided = _eventPublisher
            .EventsOfType<ApprovalDecided>()
            .LastOrDefault(e => e.Decision == ApprovalDecision.Approved);

        if (approvalDecided is null)
            return;

        await RunPaymentAsync(approvalDecided, ct);
    }

    /// <summary>Runs the payment step for a specific <see cref="ApprovalDecided"/> event.</summary>
    public async Task RunPaymentAsync(ApprovalDecided approvalDecided, CancellationToken ct = default)
    {
        var processor = new PaymentProcessor(
            _watchRepo,
            _matchRepo,
            _transactionRepo,
            _paymentProvider,
            _eventPublisher,
            NullLogger<PaymentProcessor>.Instance);

        await processor.ProcessAsync(approvalDecided, ct);
    }

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
}
