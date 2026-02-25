using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.ValueObjects;

public sealed record StatusChange(
    WatchStatus From,
    WatchStatus To,
    DateTimeOffset ChangedAt,
    string? Reason);
