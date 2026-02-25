namespace AgentPayWatch.Domain.Events;

public sealed record PaymentFailed(
    Guid MessageId,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    string Source,
    Guid TransactionId,
    string Reason);
