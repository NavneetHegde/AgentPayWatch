namespace AgentPayWatch.Domain.Events;

public sealed record PaymentCompleted(
    Guid MessageId,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    string Source,
    Guid TransactionId,
    decimal Amount,
    string Currency,
    string Merchant);
