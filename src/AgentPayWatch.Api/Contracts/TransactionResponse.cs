using AgentPayWatch.Domain.Entities;

namespace AgentPayWatch.Api.Contracts;

public sealed record TransactionResponse(
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
    string? FailureReason)
{
    public static TransactionResponse FromEntity(PaymentTransaction entity) => new(
        Id: entity.Id,
        MatchId: entity.MatchId,
        ApprovalId: entity.ApprovalId,
        WatchRequestId: entity.WatchRequestId,
        UserId: entity.UserId,
        IdempotencyKey: entity.IdempotencyKey,
        Amount: entity.Amount,
        Currency: entity.Currency,
        Merchant: entity.Merchant,
        Status: entity.Status.ToString(),
        PaymentProviderRef: entity.PaymentProviderRef,
        InitiatedAt: entity.InitiatedAt,
        CompletedAt: entity.CompletedAt,
        FailureReason: entity.FailureReason);
}
