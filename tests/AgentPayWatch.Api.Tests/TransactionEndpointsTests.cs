using System.Net;
using System.Net.Http.Json;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using NSubstitute;
using Xunit;

namespace AgentPayWatch.Api.Tests;

public sealed class TransactionEndpointsTests : IClassFixture<ApiTestFactory>
{
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public TransactionEndpointsTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── GET /api/transactions ─────────────────────────────────────────────────

    [Fact]
    public async Task GetTransactions_Returns200WithList_WhenTransactionsExist()
    {
        var tx = MakeTransaction("user-1");
        _factory.TransactionRepository
            .GetByUserIdAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new List<PaymentTransaction> { tx });

        var response = await _client.GetAsync("/api/transactions?userId=user-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<TransactionDto>>();
        Assert.NotNull(body);
        Assert.Single(body);
        Assert.Equal(tx.Id, body[0].Id);
        Assert.Equal("Succeeded", body[0].Status);
    }

    [Fact]
    public async Task GetTransactions_Returns200WithEmptyList_WhenNoTransactions()
    {
        _factory.TransactionRepository
            .GetByUserIdAsync("empty-user", Arg.Any<CancellationToken>())
            .Returns(new List<PaymentTransaction>());

        var response = await _client.GetAsync("/api/transactions?userId=empty-user");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<List<TransactionDto>>();
        Assert.NotNull(body);
        Assert.Empty(body);
    }

    [Fact]
    public async Task GetTransactions_Returns400_WhenUserIdMissing()
    {
        var response = await _client.GetAsync("/api/transactions?userId=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetTransactions_ReturnsMostRecentFirst()
    {
        var older = MakeTransaction("user-1", initiatedAt: DateTimeOffset.UtcNow.AddHours(-2));
        var newer = MakeTransaction("user-1", initiatedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var newest = MakeTransaction("user-1", initiatedAt: DateTimeOffset.UtcNow);

        _factory.TransactionRepository
            .GetByUserIdAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new List<PaymentTransaction> { older, newest, newer }); // unsorted

        var response = await _client.GetAsync("/api/transactions?userId=user-1");

        var body = await response.Content.ReadFromJsonAsync<List<TransactionDto>>();
        Assert.NotNull(body);
        Assert.Equal(3, body.Count);
        // Should be sorted newest→oldest
        Assert.Equal(newest.Id, body[0].Id);
        Assert.Equal(newer.Id, body[1].Id);
        Assert.Equal(older.Id, body[2].Id);
    }

    [Fact]
    public async Task GetTransactions_ResponseContainsAllFields()
    {
        var tx = MakeTransaction("user-1");
        _factory.TransactionRepository
            .GetByUserIdAsync("user-1", Arg.Any<CancellationToken>())
            .Returns(new List<PaymentTransaction> { tx });

        var response = await _client.GetAsync("/api/transactions?userId=user-1");
        var body = await response.Content.ReadFromJsonAsync<List<TransactionDto>>();
        Assert.NotNull(body);

        var dto = body[0];
        Assert.Equal(tx.Id, dto.Id);
        Assert.Equal(tx.MatchId, dto.MatchId);
        Assert.Equal(tx.ApprovalId, dto.ApprovalId);
        Assert.Equal(tx.WatchRequestId, dto.WatchRequestId);
        Assert.Equal(tx.UserId, dto.UserId);
        Assert.Equal(tx.IdempotencyKey, dto.IdempotencyKey);
        Assert.Equal(tx.Amount, dto.Amount);
        Assert.Equal(tx.Currency, dto.Currency);
        Assert.Equal(tx.Merchant, dto.Merchant);
        Assert.Equal("Succeeded", dto.Status);
        Assert.Equal(tx.PaymentProviderRef, dto.PaymentProviderRef);
        Assert.NotNull(dto.CompletedAt);
        Assert.Null(dto.FailureReason);
    }

    // ── GET /api/transactions/{id} ────────────────────────────────────────────

    [Fact]
    public async Task GetTransactionById_Returns200_WhenFound()
    {
        var tx = MakeTransaction("user-1");
        _factory.TransactionRepository
            .GetByIdAsync(tx.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(tx);

        var response = await _client.GetAsync($"/api/transactions/{tx.Id}?userId=user-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<TransactionDto>();
        Assert.NotNull(body);
        Assert.Equal(tx.Id, body.Id);
    }

    [Fact]
    public async Task GetTransactionById_Returns404_WhenNotFound()
    {
        var id = Guid.NewGuid();
        _factory.TransactionRepository
            .GetByIdAsync(id, "user-1", Arg.Any<CancellationToken>())
            .Returns((PaymentTransaction?)null);

        var response = await _client.GetAsync($"/api/transactions/{id}?userId=user-1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTransactionById_Returns400_WhenUserIdMissing()
    {
        var response = await _client.GetAsync($"/api/transactions/{Guid.NewGuid()}?userId=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetTransactionById_FailedTransaction_HasFailureReasonSet()
    {
        var tx = MakeTransaction("user-1", status: PaymentStatus.Failed, failureReason: "Insufficient funds");
        _factory.TransactionRepository
            .GetByIdAsync(tx.Id, "user-1", Arg.Any<CancellationToken>())
            .Returns(tx);

        var response = await _client.GetAsync($"/api/transactions/{tx.Id}?userId=user-1");
        var body = await response.Content.ReadFromJsonAsync<TransactionDto>();

        Assert.NotNull(body);
        Assert.Equal("Failed", body.Status);
        Assert.Equal("Insufficient funds", body.FailureReason);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PaymentTransaction MakeTransaction(
        string userId,
        PaymentStatus status = PaymentStatus.Succeeded,
        string? failureReason = null,
        DateTimeOffset? initiatedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            MatchId = Guid.NewGuid(),
            ApprovalId = Guid.NewGuid(),
            WatchRequestId = Guid.NewGuid(),
            UserId = userId,
            IdempotencyKey = $"{Guid.NewGuid()}:{Guid.NewGuid()}",
            Amount = 849.99m,
            Currency = "USD",
            Merchant = "TechDeals Direct",
            Status = status,
            PaymentProviderRef = status == PaymentStatus.Succeeded ? $"PAY-{Guid.NewGuid():N}" : string.Empty,
            InitiatedAt = initiatedAt ?? DateTimeOffset.UtcNow,
            CompletedAt = status == PaymentStatus.Succeeded ? DateTimeOffset.UtcNow : null,
            FailureReason = failureReason
        };
}

// ── Response DTO for deserialization ─────────────────────────────────────────

file sealed record TransactionDto(
    Guid Id,
    Guid MatchId,
    Guid ApprovalId,
    Guid WatchRequestId,
    string UserId,
    string IdempotencyKey,
    decimal Amount,
    string Currency,
    string Merchant,
    string Status,
    string PaymentProviderRef,
    DateTimeOffset InitiatedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason);
