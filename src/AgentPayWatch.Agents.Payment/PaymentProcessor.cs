using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Agents.Payment;

/// <summary>
/// Encapsulates the business logic for processing an approved payment.
/// Extracted from PaymentWorker to allow direct unit testing without Service Bus infrastructure.
/// </summary>
public sealed class PaymentProcessor
{
    private readonly IWatchRequestRepository _watchRepo;
    private readonly IProductMatchRepository _matchRepo;
    private readonly IPaymentTransactionRepository _transactionRepo;
    private readonly IPaymentProvider _paymentProvider;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<PaymentProcessor> _logger;

    public PaymentProcessor(
        IWatchRequestRepository watchRepo,
        IProductMatchRepository matchRepo,
        IPaymentTransactionRepository transactionRepo,
        IPaymentProvider paymentProvider,
        IEventPublisher eventPublisher,
        ILogger<PaymentProcessor> logger)
    {
        _watchRepo = watchRepo;
        _matchRepo = matchRepo;
        _transactionRepo = transactionRepo;
        _paymentProvider = paymentProvider;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    public async Task ProcessAsync(ApprovalDecided approvalEvent, CancellationToken ct)
    {
        var watchRequestId = approvalEvent.CorrelationId;
        var matchId = approvalEvent.MatchId;
        var approvalId = approvalEvent.ApprovalId;

        _logger.LogInformation(
            "Processing approved payment for watch {WatchId}, match {MatchId}, approval {ApprovalId}",
            watchRequestId, matchId, approvalId);

        var approvedWatches = await _watchRepo.GetByStatusAsync(WatchStatus.Approved);
        var approvedWatch = approvedWatches.FirstOrDefault(w => w.Id == watchRequestId);

        if (approvedWatch is null)
        {
            _logger.LogWarning(
                "Watch request {WatchId} not found in Approved status. It may have already been processed.",
                watchRequestId);
            return;
        }

        // Point-read to obtain the current ETag for optimistic concurrency.
        var watch = await _watchRepo.GetByIdAsync(watchRequestId, approvedWatch.UserId, ct) ?? approvedWatch;

        var match = await _matchRepo.GetByIdAsync(matchId, watchRequestId);
        if (match is null)
        {
            _logger.LogWarning(
                "Product match {MatchId} not found for watch {WatchId}", matchId, watchRequestId);
            return;
        }

        watch.UpdateStatus(WatchStatus.Purchasing, "Payment initiated");
        await _watchRepo.UpdateAsync(watch);

        _logger.LogInformation(
            "Watch {WatchId} transitioned to Purchasing. Executing payment: {Amount} {Currency} to {Seller}",
            watch.Id, match.Price, match.Currency, match.Seller);

        var idempotencyKey = $"{matchId}:{approvalId}";
        var paymentMethodToken = string.IsNullOrEmpty(watch.PaymentMethodToken)
            ? "tok_demo"
            : watch.PaymentMethodToken;

        var paymentResult = await _paymentProvider.ExecutePaymentAsync(
            idempotencyKey, match.Price, match.Currency, match.Seller, paymentMethodToken, ct);

        if (paymentResult.Success)
            await HandlePaymentSuccessAsync(watch, match, approvalId, idempotencyKey, paymentResult, ct);
        else
            await HandlePaymentFailureAsync(watch, match, approvalId, idempotencyKey, paymentResult, ct);
    }

    private async Task HandlePaymentSuccessAsync(
        WatchRequest watch,
        ProductMatch match,
        Guid approvalId,
        string idempotencyKey,
        PaymentResult paymentResult,
        CancellationToken ct)
    {
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            ApprovalId = approvalId,
            WatchRequestId = watch.Id,
            UserId = watch.UserId,
            IdempotencyKey = idempotencyKey,
            Amount = match.Price,
            Currency = match.Currency,
            Merchant = match.Seller,
            Status = PaymentStatus.Succeeded,
            PaymentProviderRef = paymentResult.ProviderReference ?? string.Empty,
            InitiatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null
        };

        await _transactionRepo.CreateAsync(transaction);

        watch.UpdateStatus(WatchStatus.Completed, $"Payment succeeded, ref: {paymentResult.ProviderReference}");
        await _watchRepo.UpdateAsync(watch);

        var paymentCompletedEvent = new PaymentCompleted(
            MessageId: Guid.NewGuid(),
            CorrelationId: watch.Id,
            Timestamp: DateTimeOffset.UtcNow,
            Source: "payment-agent",
            TransactionId: transaction.Id,
            Amount: match.Price,
            Currency: match.Currency,
            Merchant: match.Seller);

        await _eventPublisher.PublishAsync(paymentCompletedEvent, TopicNames.PaymentCompleted, ct);

        _logger.LogInformation(
            "Payment succeeded for watch {WatchId}: {Amount} {Currency} to {Merchant}, ref: {ProviderRef}",
            watch.Id, match.Price, match.Currency, match.Seller, paymentResult.ProviderReference);
    }

    private async Task HandlePaymentFailureAsync(
        WatchRequest watch,
        ProductMatch match,
        Guid approvalId,
        string idempotencyKey,
        PaymentResult paymentResult,
        CancellationToken ct)
    {
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            ApprovalId = approvalId,
            WatchRequestId = watch.Id,
            UserId = watch.UserId,
            IdempotencyKey = idempotencyKey,
            Amount = match.Price,
            Currency = match.Currency,
            Merchant = match.Seller,
            Status = PaymentStatus.Failed,
            PaymentProviderRef = string.Empty,
            InitiatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            FailureReason = paymentResult.FailureReason
        };

        await _transactionRepo.CreateAsync(transaction);

        watch.UpdateStatus(WatchStatus.Active, $"Payment failed: {paymentResult.FailureReason}. Returning to active scanning.");
        await _watchRepo.UpdateAsync(watch);

        var paymentFailedEvent = new PaymentFailed(
            MessageId: Guid.NewGuid(),
            CorrelationId: watch.Id,
            Timestamp: DateTimeOffset.UtcNow,
            Source: "payment-agent",
            TransactionId: transaction.Id,
            Reason: paymentResult.FailureReason ?? "Unknown failure");

        await _eventPublisher.PublishAsync(paymentFailedEvent, TopicNames.PaymentFailed, ct);

        _logger.LogInformation(
            "Payment failed for watch {WatchId}: {Reason}. Watch returned to Active for re-scanning.",
            watch.Id, paymentResult.FailureReason);
    }
}
