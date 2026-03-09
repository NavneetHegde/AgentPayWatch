using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentPayWatch.Api.Endpoints;

public static class MatchEndpoints
{
    public static void MapMatchEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/matches");

        group.MapGet("/{watchId:guid}", GetMatchesByWatchId)
            .WithName("GetMatchesByWatchId")
            .WithOpenApi();
    }

    private static async Task<Ok<List<MatchResponse>>> GetMatchesByWatchId(
        Guid watchId,
        IProductMatchRepository matchRepo,
        IApprovalRepository approvalRepo,
        CancellationToken ct)
    {
        IReadOnlyList<ProductMatch> matches = await matchRepo.GetByWatchRequestIdAsync(watchId, ct);

        var responses = new List<MatchResponse>();

        foreach (var match in matches)
        {
            string? approvalToken = null;
            string? approvalDecision = null;
            DateTimeOffset? approvalExpiresAt = null;

            ApprovalRecord? approval = await approvalRepo.GetByMatchIdAsync(match.Id, match.WatchRequestId, ct);
            if (approval is not null)
            {
                // Only expose the token if the approval is still pending and not expired
                if (approval.Decision == ApprovalDecision.Pending
                    && approval.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    approvalToken = approval.ApprovalToken;
                }

                approvalDecision = approval.Decision.ToString();
                approvalExpiresAt = approval.ExpiresAt;
            }

            responses.Add(new MatchResponse(
                Id: match.Id,
                WatchRequestId: match.WatchRequestId,
                UserId: match.UserId,
                ProductName: match.ProductName,
                Price: match.Price,
                Currency: match.Currency,
                Seller: match.Seller,
                ProductUrl: match.ProductUrl,
                MatchedAt: match.MatchedAt,
                ExpiresAt: match.ExpiresAt,
                Availability: match.Availability,
                ApprovalToken: approvalToken,
                ApprovalDecision: approvalDecision,
                ApprovalExpiresAt: approvalExpiresAt
            ));
        }

        return TypedResults.Ok(responses);
    }
}
