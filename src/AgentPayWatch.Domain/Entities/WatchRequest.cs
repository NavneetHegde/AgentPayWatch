using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.ValueObjects;

namespace AgentPayWatch.Domain.Entities;

public sealed class WatchRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal MaxPrice { get; set; }
    public string Currency { get; set; } = "USD";
    public string[] PreferredSellers { get; set; } = [];
    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.AlwaysAsk;
    public decimal? AutoApproveThreshold { get; set; }
    public string PaymentMethodToken { get; set; } = string.Empty;
    public NotificationChannel NotificationChannel { get; set; } = NotificationChannel.A2P_SMS;
    public string PhoneNumber { get; set; } = string.Empty;
    public WatchStatus Status { get; set; } = WatchStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<StatusChange> StatusHistory { get; set; } = [];
    [JsonIgnore]
    public string ETag { get; set; } = string.Empty;

    private static readonly Dictionary<WatchStatus, HashSet<WatchStatus>> AllowedTransitions = new()
    {
        [WatchStatus.Active] = [WatchStatus.Paused, WatchStatus.Matched, WatchStatus.Expired, WatchStatus.Cancelled],
        [WatchStatus.Paused] = [WatchStatus.Active, WatchStatus.Cancelled],
        [WatchStatus.Matched] = [WatchStatus.AwaitingApproval, WatchStatus.Active, WatchStatus.Cancelled],
        [WatchStatus.AwaitingApproval] = [WatchStatus.Approved, WatchStatus.Active, WatchStatus.Cancelled],
        [WatchStatus.Approved] = [WatchStatus.Purchasing, WatchStatus.Cancelled],
        [WatchStatus.Purchasing] = [WatchStatus.Completed, WatchStatus.Active],
        [WatchStatus.Completed] = [],
        [WatchStatus.Expired] = [],
        [WatchStatus.Cancelled] = []
    };

    public void UpdateStatus(WatchStatus newStatus, string? reason = null)
    {
        if (Status == newStatus)
        {
            return;
        }

        if (!AllowedTransitions.TryGetValue(Status, out var allowed) || !allowed.Contains(newStatus))
        {
            throw new InvalidOperationException(
                $"Cannot transition from {Status} to {newStatus}.");
        }

        var change = new StatusChange(Status, newStatus, DateTimeOffset.UtcNow, reason);
        StatusHistory.Add(change);
        Status = newStatus;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
