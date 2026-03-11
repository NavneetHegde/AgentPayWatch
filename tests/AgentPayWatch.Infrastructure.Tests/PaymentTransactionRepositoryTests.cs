using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Infrastructure.Cosmos;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace AgentPayWatch.Infrastructure.Tests;

[Collection("CosmosIntegration")]
public sealed class PaymentTransactionRepositoryTests : IAsyncLifetime
{
    private readonly CosmosFixture _fixture;
    private CosmosPaymentTransactionRepository _repo = null!;

    public PaymentTransactionRepositoryTests(CosmosFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        Skip.If(!_fixture.IsAvailable,
            $"Cosmos DB emulator unreachable — start Aspire first. Reason: {_fixture.UnavailableReason}");
        _repo = new CosmosPaymentTransactionRepository(_fixture.Client);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_fixture.IsAvailable)
            await _fixture.DeleteAllTransactionDocumentsAsync();
    }

    // ── CreateAsync ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateAsync_StoresDocumentInTransactionsContainer()
    {
        var tx = MakeTransaction("user-tx-1");

        await _repo.CreateAsync(tx);

        // Direct read to verify partition key (userId as string) and field mapping.
        var response = await _fixture.TransactionsContainer.ReadItemAsync<dynamic>(
            tx.Id.ToString(), new PartitionKey(tx.UserId));
        dynamic doc = response.Resource;

        Assert.Equal(tx.Id.ToString(), (string)doc.id);
        Assert.Equal("user-tx-1", (string)doc.userId);
        Assert.Equal(tx.WatchRequestId.ToString(), (string)doc.watchRequestId);
        Assert.Equal(tx.MatchId.ToString(), (string)doc.matchId);
        Assert.Equal(tx.ApprovalId.ToString(), (string)doc.approvalId);
        Assert.Equal("succeeded", (string)doc.status);   // enum → camelCase string
        Assert.Equal(199.99m, (decimal)doc.amount);
        Assert.Equal("USD", (string)doc.currency);
        Assert.Equal("BestMerchant", (string)doc.merchant);
        Assert.Equal("PAY-REF-001", (string)doc.paymentProviderRef);
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetByIdAsync_ReturnsStoredTransaction()
    {
        var tx = MakeTransaction("user-get-1");
        await _repo.CreateAsync(tx);

        var fetched = await _repo.GetByIdAsync(tx.Id, tx.UserId);

        Assert.NotNull(fetched);
        Assert.Equal(tx.Id, fetched.Id);
        Assert.Equal("user-get-1", fetched.UserId);
        Assert.Equal(tx.WatchRequestId, fetched.WatchRequestId);
        Assert.Equal(tx.MatchId, fetched.MatchId);
        Assert.Equal(tx.ApprovalId, fetched.ApprovalId);
        Assert.Equal(PaymentStatus.Succeeded, fetched.Status);
        Assert.Equal(199.99m, fetched.Amount);
        Assert.Equal("USD", fetched.Currency);
        Assert.Equal("BestMerchant", fetched.Merchant);
        Assert.Equal("PAY-REF-001", fetched.PaymentProviderRef);
        Assert.NotNull(fetched.CompletedAt);
        Assert.Null(fetched.FailureReason);
    }

    [SkippableFact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid(), "user-nobody");
        Assert.Null(result);
    }

    [SkippableFact]
    public async Task GetByIdAsync_ReturnsNull_WhenUserIdDoesNotMatchPartition()
    {
        var tx = MakeTransaction("user-partition-check");
        await _repo.CreateAsync(tx);

        // Correct id, wrong userId (partition key) → point read misses.
        var result = await _repo.GetByIdAsync(tx.Id, "wrong-user");
        Assert.Null(result);
    }

    // ── GetByUserIdAsync ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task GetByUserIdAsync_ReturnsAllTransactionsForUser()
    {
        await _repo.CreateAsync(MakeTransaction("user-multi-1", amount: 10m));
        await _repo.CreateAsync(MakeTransaction("user-multi-1", amount: 20m));
        await _repo.CreateAsync(MakeTransaction("user-multi-2", amount: 30m));

        var results = await _repo.GetByUserIdAsync("user-multi-1");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("user-multi-1", r.UserId));
    }

    [SkippableFact]
    public async Task GetByUserIdAsync_ReturnsEmpty_WhenUserHasNoTransactions()
    {
        var results = await _repo.GetByUserIdAsync("user-with-no-txns");
        Assert.Empty(results);
    }

    [SkippableFact]
    public async Task GetByUserIdAsync_DoesNotReturnTransactionsFromOtherUsers()
    {
        await _repo.CreateAsync(MakeTransaction("user-isolation-a"));
        await _repo.CreateAsync(MakeTransaction("user-isolation-b"));

        var resultsA = await _repo.GetByUserIdAsync("user-isolation-a");
        var resultsB = await _repo.GetByUserIdAsync("user-isolation-b");

        Assert.Single(resultsA);
        Assert.Single(resultsB);
        Assert.NotEqual(resultsA[0].Id, resultsB[0].Id);
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task UpdateAsync_PersistsStatusChange_SucceededToFailed()
    {
        var tx = MakeTransaction("user-update-1");
        await _repo.CreateAsync(tx);

        tx.Status = PaymentStatus.Failed;
        tx.FailureReason = "Card declined";
        tx.CompletedAt = null;
        await _repo.UpdateAsync(tx);

        var fetched = await _repo.GetByIdAsync(tx.Id, tx.UserId);

        Assert.NotNull(fetched);
        Assert.Equal(PaymentStatus.Failed, fetched.Status);
        Assert.Equal("Card declined", fetched.FailureReason);
        Assert.Null(fetched.CompletedAt);
    }

    [SkippableFact]
    public async Task UpdateAsync_PersistsProviderRef()
    {
        var tx = MakeTransaction("user-update-2", providerRef: string.Empty);
        tx.Status = PaymentStatus.Initiated;
        await _repo.CreateAsync(tx);

        tx.Status = PaymentStatus.Succeeded;
        tx.PaymentProviderRef = "PAY-FINAL-999";
        tx.CompletedAt = DateTimeOffset.UtcNow;
        await _repo.UpdateAsync(tx);

        var fetched = await _repo.GetByIdAsync(tx.Id, tx.UserId);

        Assert.NotNull(fetched);
        Assert.Equal(PaymentStatus.Succeeded, fetched.Status);
        Assert.Equal("PAY-FINAL-999", fetched.PaymentProviderRef);
        Assert.NotNull(fetched.CompletedAt);
    }

    // ── Idempotency key ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task CreateAsync_PreservesIdempotencyKey()
    {
        var tx = MakeTransaction("user-idemp-1");
        tx.IdempotencyKey = "match-abc:approval-xyz";

        await _repo.CreateAsync(tx);

        var fetched = await _repo.GetByIdAsync(tx.Id, tx.UserId);

        Assert.NotNull(fetched);
        Assert.Equal("match-abc:approval-xyz", fetched.IdempotencyKey);
    }

    // ── Failed transaction (no CompletedAt, no ProviderRef) ──────────────────

    [SkippableFact]
    public async Task CreateAsync_FailedTransaction_NullCompletedAtAndEmptyRef()
    {
        var tx = MakeTransaction("user-fail-1");
        tx.Status = PaymentStatus.Failed;
        tx.CompletedAt = null;
        tx.PaymentProviderRef = string.Empty;
        tx.FailureReason = "Insufficient funds";

        await _repo.CreateAsync(tx);

        var fetched = await _repo.GetByIdAsync(tx.Id, tx.UserId);

        Assert.NotNull(fetched);
        Assert.Equal(PaymentStatus.Failed, fetched.Status);
        Assert.Null(fetched.CompletedAt);
        Assert.Equal(string.Empty, fetched.PaymentProviderRef);
        Assert.Equal("Insufficient funds", fetched.FailureReason);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PaymentTransaction MakeTransaction(
        string userId,
        decimal amount = 199.99m,
        string providerRef = "PAY-REF-001") => new()
    {
        Id = Guid.NewGuid(),
        MatchId = Guid.NewGuid(),
        ApprovalId = Guid.NewGuid(),
        WatchRequestId = Guid.NewGuid(),
        UserId = userId,
        IdempotencyKey = $"{Guid.NewGuid()}:{Guid.NewGuid()}",
        Amount = amount,
        Currency = "USD",
        Merchant = "BestMerchant",
        Status = PaymentStatus.Succeeded,
        PaymentProviderRef = providerRef,
        InitiatedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        FailureReason = null
    };
}
