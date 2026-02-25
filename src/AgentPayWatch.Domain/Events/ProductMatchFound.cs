namespace AgentPayWatch.Domain.Events;

public sealed record ProductMatchFound(
    Guid MessageId,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    string Source,
    Guid MatchId,
    string ProductName,
    decimal Price,
    string Currency,
    string Seller);
