using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Agents.Approval;

public sealed class ApprovalTimeoutWorker : BackgroundService
{
    private readonly IApprovalRepository _approvalRepo;
    private readonly IWatchRequestRepository _watchRepo;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<ApprovalTimeoutWorker> _logger;
    private readonly TimeSpan _checkInterval;

    public ApprovalTimeoutWorker(
        IApprovalRepository approvalRepo,
        IWatchRequestRepository watchRepo,
        IEventPublisher eventPublisher,
        ILogger<ApprovalTimeoutWorker> logger,
        IConfiguration configuration)
    {
        _approvalRepo = approvalRepo;
        _watchRepo = watchRepo;
        _eventPublisher = eventPublisher;
        _logger = logger;

        int intervalSeconds = configuration.GetValue<int>(
            "Approval:TimeoutCheckIntervalSeconds", 30);
        _checkInterval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ApprovalTimeoutWorker started. Check interval: {IntervalSeconds}s",
            _checkInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckExpiredApprovalsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking expired approvals. Will retry next interval.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("ApprovalTimeoutWorker stopped.");
    }

    private async Task CheckExpiredApprovalsAsync(CancellationToken ct)
    {
        IReadOnlyList<ApprovalRecord> expiredApprovals =
            await _approvalRepo.GetPendingExpiredAsync(DateTimeOffset.UtcNow, ct);

        if (expiredApprovals.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Found {Count} expired pending approval(s). Processing...",
            expiredApprovals.Count);

        foreach (var approval in expiredApprovals)
        {
            try
            {
                await ProcessExpiredApprovalAsync(approval, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing expired approval {ApprovalId} for watch {WatchRequestId}. Skipping.",
                    approval.Id,
                    approval.WatchRequestId);
            }
        }
    }

    private async Task ProcessExpiredApprovalAsync(ApprovalRecord approval, CancellationToken ct)
    {
        // Mark the approval as expired
        approval.Decision = ApprovalDecision.Expired;
        approval.RespondedAt = DateTimeOffset.UtcNow;
        await _approvalRepo.UpdateAsync(approval, ct);

        // Load the watch and reactivate it so the Product Watch Agent re-scans
        WatchRequest? watch = await _watchRepo.GetByIdAsync(
            approval.WatchRequestId,
            approval.UserId,
            ct);

        if (watch is not null)
        {
            watch.UpdateStatus(WatchStatus.Active);
            await _watchRepo.UpdateAsync(watch, ct);
        }

        // Publish ApprovalDecided event with Expired decision
        var decidedEvent = new ApprovalDecided(
            MessageId: Guid.NewGuid(),
            CorrelationId: approval.WatchRequestId,
            Timestamp: DateTimeOffset.UtcNow,
            Source: "approval-agent",
            ApprovalId: approval.Id,
            MatchId: approval.MatchId,
            Decision: ApprovalDecision.Expired
        );

        await _eventPublisher.PublishAsync(decidedEvent, TopicNames.ApprovalDecided, ct);

        _logger.LogInformation(
            "Approval {ApprovalId} expired for watch {WatchRequestId}. Watch reactivated.",
            approval.Id,
            approval.WatchRequestId);
    }
}
