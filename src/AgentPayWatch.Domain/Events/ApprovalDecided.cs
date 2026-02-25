using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Events;

public sealed record ApprovalDecided(
    Guid MessageId,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    string Source,
    Guid ApprovalId,
    Guid MatchId,
    ApprovalDecision Decision);
