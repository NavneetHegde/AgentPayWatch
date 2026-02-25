using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Entities;

public sealed class ApprovalRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MatchId { get; set; }
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ApprovalToken { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public ApprovalDecision Decision { get; set; } = ApprovalDecision.Pending;
    public NotificationChannel Channel { get; set; } = NotificationChannel.A2P_SMS;
}
