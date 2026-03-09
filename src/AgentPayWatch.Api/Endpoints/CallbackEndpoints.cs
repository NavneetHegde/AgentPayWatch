using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentPayWatch.Api.Endpoints;

public static class CallbackEndpoints
{
    public static void MapCallbackEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/a2p");

        group.MapPost("/callback", HandleApprovalCallback)
            .WithName("ApprovalCallback")
            .WithOpenApi();
    }

    private static async Task<Results<Ok<ApprovalCallbackResponse>, NotFound<string>, BadRequest<string>>>
        HandleApprovalCallback(
            ApprovalCallbackRequest request,
            IApprovalRepository approvalRepo,
            IWatchRequestRepository watchRepo,
            IEventPublisher eventPublisher,
            CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return TypedResults.BadRequest("Token is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Decision))
        {
            return TypedResults.BadRequest("Decision is required. Use 'BUY' or 'SKIP'.");
        }

        var approval = await approvalRepo.GetByTokenAsync(request.Token, ct);
        if (approval is null)
        {
            return TypedResults.NotFound("Approval token not found.");
        }

        if (approval.Decision != ApprovalDecision.Pending)
        {
            return TypedResults.BadRequest(
                $"Approval has already been resolved with decision: {approval.Decision}.");
        }

        if (approval.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return TypedResults.BadRequest("Approval token has expired.");
        }

        ApprovalDecision decision = request.Decision.ToUpperInvariant() switch
        {
            "BUY" => ApprovalDecision.Approved,
            "SKIP" => ApprovalDecision.Rejected,
            _ => ApprovalDecision.Pending // sentinel for invalid input
        };

        if (decision == ApprovalDecision.Pending)
        {
            return TypedResults.BadRequest(
                $"Invalid decision '{request.Decision}'. Use 'BUY' to approve or 'SKIP' to reject.");
        }

        approval.Decision = decision;
        approval.RespondedAt = DateTimeOffset.UtcNow;
        await approvalRepo.UpdateAsync(approval, ct);

        var watch = await watchRepo.GetByIdAsync(approval.WatchRequestId, approval.UserId, ct);
        if (watch is not null)
        {
            if (decision == ApprovalDecision.Approved)
            {
                watch.UpdateStatus(WatchStatus.Approved);
            }
            else
            {
                watch.UpdateStatus(WatchStatus.Active);
            }

            await watchRepo.UpdateAsync(watch, ct);
        }

        var decidedEvent = new ApprovalDecided(
            MessageId: Guid.NewGuid(),
            CorrelationId: approval.WatchRequestId,
            Timestamp: DateTimeOffset.UtcNow,
            Source: "api-callback",
            ApprovalId: approval.Id,
            MatchId: approval.MatchId,
            Decision: decision
        );

        await eventPublisher.PublishAsync(decidedEvent, TopicNames.ApprovalDecided, ct);

        var response = new ApprovalCallbackResponse(
            ApprovalId: approval.Id,
            Decision: decision.ToString(),
            WatchRequestId: approval.WatchRequestId,
            RespondedAt: approval.RespondedAt!.Value
        );

        return TypedResults.Ok(response);
    }
}

public sealed record ApprovalCallbackResponse(
    Guid ApprovalId,
    string Decision,
    Guid WatchRequestId,
    DateTimeOffset RespondedAt
);
