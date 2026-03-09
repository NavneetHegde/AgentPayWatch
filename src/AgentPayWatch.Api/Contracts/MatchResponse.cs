using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Api.Contracts;

public sealed record MatchResponse(
    Guid Id,
    Guid WatchRequestId,
    string UserId,
    string ProductName,
    decimal Price,
    string Currency,
    string Seller,
    string ProductUrl,
    DateTimeOffset MatchedAt,
    DateTimeOffset ExpiresAt,
    ProductAvailability Availability,
    string? ApprovalToken,
    string? ApprovalDecision,
    DateTimeOffset? ApprovalExpiresAt
);
