using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Entities;

public sealed class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MatchId { get; set; }
    public Guid ApprovalId { get; set; }
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Merchant { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; } = PaymentStatus.Initiated;
    public string PaymentProviderRef { get; set; } = string.Empty;
    public DateTimeOffset InitiatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
}
