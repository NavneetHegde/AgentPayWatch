using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPayWatch.Agents.Payment.Tests;

public sealed class PaymentProcessorTests
{
    // ── Watch not found ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_DoesNothing_WhenWatchNotInApprovedStatus()
    {
        // Watch exists but in wrong status (Active, not Approved)
        var watch = MakeWatch(WatchStatus.Active);
        var (transactionRepo, publisher) = await RunAsync(
            approvalEvent: MakeApprovalEvent(watchId: watch.Id),
            watches: [watch],
            matches: []);

        Assert.Empty(transactionRepo.CreatedTransactions);
        Assert.Empty(publisher.PublishedEvents);
    }

    [Fact]
    public async Task ProcessAsync_DoesNothing_WhenWatchIdNotFound()
    {
        var (transactionRepo, publisher) = await RunAsync(
            approvalEvent: MakeApprovalEvent(watchId: Guid.NewGuid()),
            watches: [],
            matches: []);

        Assert.Empty(transactionRepo.CreatedTransactions);
        Assert.Empty(publisher.PublishedEvents);
    }

    // ── Match not found ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_DoesNothing_WhenMatchNotFound()
    {
        var watch = MakeWatch(WatchStatus.Approved);
        var approvalEvent = MakeApprovalEvent(watchId: watch.Id, matchId: Guid.NewGuid());

        var (transactionRepo, publisher) = await RunAsync(approvalEvent, [watch], []);

        Assert.Empty(transactionRepo.CreatedTransactions);
        Assert.Empty(publisher.PublishedEvents);
    }

    // ── Successful payment ────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_CreatesTransaction_OnPaymentSuccess()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var (transactionRepo, _) = await RunAsync(approvalEvent, [watch], [match], succeed: true);

        Assert.Single(transactionRepo.CreatedTransactions);
        var tx = transactionRepo.CreatedTransactions[0];
        Assert.Equal(PaymentStatus.Succeeded, tx.Status);
        Assert.Equal(match.Id, tx.MatchId);
        Assert.Equal(approvalEvent.ApprovalId, tx.ApprovalId);
        Assert.Equal(watch.Id, tx.WatchRequestId);
        Assert.Equal(watch.UserId, tx.UserId);
        Assert.Equal(match.Price, tx.Amount);
        Assert.Equal(match.Currency, tx.Currency);
        Assert.Equal(match.Seller, tx.Merchant);
        Assert.NotNull(tx.CompletedAt);
        Assert.Null(tx.FailureReason);
    }

    [Fact]
    public async Task ProcessAsync_TransitionsWatchToCompleted_OnPaymentSuccess()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var watchRepo = new FakeWatchRequestRepository(watch);

        await RunWithRepoAsync(approvalEvent, watchRepo, [match], succeed: true);

        Assert.Equal(WatchStatus.Completed, watchRepo.GetById(watch.Id)!.Status);
    }

    [Fact]
    public async Task ProcessAsync_PublishesPaymentCompletedEvent_OnPaymentSuccess()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var (_, publisher) = await RunAsync(approvalEvent, [watch], [match], succeed: true);

        Assert.Single(publisher.PublishedEvents);
        var (evt, topic) = publisher.PublishedEvents[0];
        Assert.IsType<PaymentCompleted>(evt);
        Assert.Equal(TopicNames.PaymentCompleted, topic);

        var completed = (PaymentCompleted)evt;
        Assert.Equal(watch.Id, completed.CorrelationId);
        Assert.Equal(match.Price, completed.Amount);
        Assert.Equal(match.Currency, completed.Currency);
        Assert.Equal(match.Seller, completed.Merchant);
        Assert.Equal("payment-agent", completed.Source);
    }

    [Fact]
    public async Task ProcessAsync_TransitionsToPurchasingBeforeCallingProvider()
    {
        // Verifies the watch passes through Purchasing on the way to Completed
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var watchRepo = new FakeWatchRequestRepository(watch);

        await RunWithRepoAsync(approvalEvent, watchRepo, [match], succeed: true);

        var finalWatch = watchRepo.GetById(watch.Id)!;
        Assert.Equal(WatchStatus.Completed, finalWatch.Status);
        // Status history confirms the Purchasing intermediate state was recorded
        Assert.Contains(finalWatch.StatusHistory, h => h.To == WatchStatus.Purchasing);
        Assert.Contains(finalWatch.StatusHistory, h => h.To == WatchStatus.Completed);
    }

    // ── Failed payment ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_CreatesFailedTransaction_OnPaymentFailure()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var (transactionRepo, _) = await RunAsync(
            approvalEvent, [watch], [match], succeed: false, failureReason: "Card declined");

        Assert.Single(transactionRepo.CreatedTransactions);
        var tx = transactionRepo.CreatedTransactions[0];
        Assert.Equal(PaymentStatus.Failed, tx.Status);
        Assert.Equal("Card declined", tx.FailureReason);
        Assert.Empty(tx.PaymentProviderRef);
        Assert.Null(tx.CompletedAt);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsWatchToActive_OnPaymentFailure()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var watchRepo = new FakeWatchRequestRepository(watch);

        await RunWithRepoAsync(approvalEvent, watchRepo, [match], succeed: false);

        Assert.Equal(WatchStatus.Active, watchRepo.GetById(watch.Id)!.Status);
    }

    [Fact]
    public async Task ProcessAsync_PublishesPaymentFailedEvent_OnPaymentFailure()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var (_, publisher) = await RunAsync(
            approvalEvent, [watch], [match], succeed: false, failureReason: "Insufficient funds");

        Assert.Single(publisher.PublishedEvents);
        var (evt, topic) = publisher.PublishedEvents[0];
        Assert.IsType<PaymentFailed>(evt);
        Assert.Equal(TopicNames.PaymentFailed, topic);

        var failed = (PaymentFailed)evt;
        Assert.Equal(watch.Id, failed.CorrelationId);
        Assert.Equal("Insufficient funds", failed.Reason);
        Assert.Equal("payment-agent", failed.Source);
    }

    [Fact]
    public async Task ProcessAsync_FailedPayment_TransitionsToPurchasingThenBackToActive()
    {
        // Verifies the watch passes through Purchasing before returning to Active
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var watchRepo = new FakeWatchRequestRepository(watch);

        await RunWithRepoAsync(approvalEvent, watchRepo, [match], succeed: false);

        var finalWatch = watchRepo.GetById(watch.Id)!;
        Assert.Equal(WatchStatus.Active, finalWatch.Status);
        // Status history confirms Purchasing was an intermediate state
        Assert.Contains(finalWatch.StatusHistory, h => h.To == WatchStatus.Purchasing);
        Assert.Contains(finalWatch.StatusHistory,
            h => h.From == WatchStatus.Purchasing && h.To == WatchStatus.Active);
    }

    // ── Idempotency key format ────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_IdempotencyKey_IsMatchIdColonApprovalId()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var paymentProvider = new FakePaymentProvider(alwaysSucceed: true);

        await RunWithProviderAsync(
            approvalEvent, new FakeWatchRequestRepository(watch), [match], paymentProvider);

        Assert.Single(paymentProvider.Calls);
        var expectedKey = $"{match.Id}:{approvalEvent.ApprovalId}";
        Assert.Equal(expectedKey, paymentProvider.Calls[0].Key);
    }

    [Fact]
    public async Task ProcessAsync_Transaction_IdempotencyKeyMatchesProviderCall()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var (transactionRepo, _) = await RunAsync(approvalEvent, [watch], [match], succeed: true);

        var tx = transactionRepo.CreatedTransactions[0];
        Assert.Equal($"{match.Id}:{approvalEvent.ApprovalId}", tx.IdempotencyKey);
    }

    // ── Payment method token ──────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_UsesFallbackToken_WhenWatchHasNoPaymentMethodToken()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        watch.PaymentMethodToken = string.Empty;
        var paymentProvider = new FakePaymentProvider(alwaysSucceed: true);

        await RunWithProviderAsync(
            approvalEvent, new FakeWatchRequestRepository(watch), [match], paymentProvider);

        Assert.Equal("tok_demo", paymentProvider.Calls[0].Token);
    }

    [Fact]
    public async Task ProcessAsync_UsesWatchToken_WhenWatchHasPaymentMethodToken()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        watch.PaymentMethodToken = "tok_real_card_xyz";
        var paymentProvider = new FakePaymentProvider(alwaysSucceed: true);

        await RunWithProviderAsync(
            approvalEvent, new FakeWatchRequestRepository(watch), [match], paymentProvider);

        Assert.Equal("tok_real_card_xyz", paymentProvider.Calls[0].Token);
    }

    // ── Success transaction details ───────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_SuccessTransaction_HasProviderRefSet()
    {
        var (watch, match, approvalEvent) = MakeApprovedScenario();
        var (transactionRepo, _) = await RunAsync(approvalEvent, [watch], [match], succeed: true);

        var tx = transactionRepo.CreatedTransactions[0];
        Assert.NotEmpty(tx.PaymentProviderRef);
        Assert.StartsWith("PAY-", tx.PaymentProviderRef);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(FakePaymentTransactionRepository, FakeEventPublisher)> RunAsync(
        ApprovalDecided approvalEvent,
        WatchRequest[] watches,
        ProductMatch[] matches,
        bool succeed = true,
        string failureReason = "Card declined")
    {
        var watchRepo = new FakeWatchRequestRepository(watches);
        var provider = new FakePaymentProvider(succeed, failureReason);
        return await RunWithProviderAsync(approvalEvent, watchRepo, matches, provider);
    }

    private static async Task RunWithRepoAsync(
        ApprovalDecided approvalEvent,
        FakeWatchRequestRepository watchRepo,
        ProductMatch[] matches,
        bool succeed = true,
        string failureReason = "Card declined")
    {
        var provider = new FakePaymentProvider(succeed, failureReason);
        await RunWithProviderAsync(approvalEvent, watchRepo, matches, provider);
    }

    private static async Task<(FakePaymentTransactionRepository, FakeEventPublisher)> RunWithProviderAsync(
        ApprovalDecided approvalEvent,
        FakeWatchRequestRepository watchRepo,
        ProductMatch[] matches,
        FakePaymentProvider paymentProvider)
    {
        var matchRepo = new FakeProductMatchRepository(matches);
        var transactionRepo = new FakePaymentTransactionRepository();
        var publisher = new FakeEventPublisher();

        var processor = new PaymentProcessor(
            watchRepo,
            matchRepo,
            transactionRepo,
            paymentProvider,
            publisher,
            NullLogger<PaymentProcessor>.Instance);

        await processor.ProcessAsync(approvalEvent, CancellationToken.None);

        return (transactionRepo, publisher);
    }

    private static ApprovalDecided MakeApprovalEvent(
        Guid? watchId = null,
        Guid? matchId = null,
        Guid? approvalId = null) =>
        new(
            MessageId: Guid.NewGuid(),
            CorrelationId: watchId ?? Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Source: "approval-agent",
            ApprovalId: approvalId ?? Guid.NewGuid(),
            MatchId: matchId ?? Guid.NewGuid(),
            Decision: ApprovalDecision.Approved);

    private static WatchRequest MakeWatch(WatchStatus status, Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            UserId = "user-1",
            ProductName = "Test Product",
            MaxPrice = 999m,
            Status = status,
            StatusHistory = status switch
            {
                WatchStatus.Approved =>
                [
                    new(WatchStatus.Active, WatchStatus.Matched, DateTimeOffset.UtcNow.AddMinutes(-30), null),
                    new(WatchStatus.Matched, WatchStatus.AwaitingApproval, DateTimeOffset.UtcNow.AddMinutes(-20), null),
                    new(WatchStatus.AwaitingApproval, WatchStatus.Approved, DateTimeOffset.UtcNow.AddMinutes(-5), null)
                ],
                _ => []
            }
        };

    private static ProductMatch MakeMatch(Guid watchRequestId) =>
        new()
        {
            Id = Guid.NewGuid(),
            WatchRequestId = watchRequestId,
            UserId = "user-1",
            ProductName = "Test Product",
            Price = 849.99m,
            Currency = "USD",
            Seller = "TechDeals Direct"
        };

    private static (WatchRequest watch, ProductMatch match, ApprovalDecided approvalEvent)
        MakeApprovedScenario()
    {
        var watch = MakeWatch(WatchStatus.Approved);
        var match = MakeMatch(watch.Id);
        var approvalEvent = MakeApprovalEvent(watchId: watch.Id, matchId: match.Id);
        return (watch, match, approvalEvent);
    }
}
