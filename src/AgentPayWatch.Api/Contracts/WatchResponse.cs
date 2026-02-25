using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Api.Contracts;

public sealed record WatchResponse(
    Guid Id,
    string UserId,
    string ProductName,
    decimal MaxPrice,
    string Currency,
    string[] PreferredSellers,
    ApprovalMode ApprovalMode,
    decimal? AutoApproveThreshold,
    NotificationChannel NotificationChannel,
    WatchStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static WatchResponse FromEntity(WatchRequest entity) => new(
        entity.Id,
        entity.UserId,
        entity.ProductName,
        entity.MaxPrice,
        entity.Currency,
        entity.PreferredSellers,
        entity.ApprovalMode,
        entity.AutoApproveThreshold,
        entity.NotificationChannel,
        entity.Status,
        entity.CreatedAt,
        entity.UpdatedAt);
}
