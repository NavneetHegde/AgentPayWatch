// ============================================================
// TEMPORARY: Remove this file in Phase 4.
// This endpoint exists to validate Service Bus connectivity.
// ============================================================

using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Messaging;

namespace AgentPayWatch.Api.Endpoints;

public static class DebugEndpoints
{
    public static IEndpointRouteBuilder MapDebugEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/debug")
            .WithTags("Debug");

        group.MapPost("/publish-test-event", PublishTestEvent)
            .WithName("PublishTestEvent")
            .WithDescription("Publishes a fake ProductMatchFound event to Service Bus for connectivity testing.");

        return routes;
    }

    private static async Task<IResult> PublishTestEvent(
        IEventPublisher publisher,
        CancellationToken ct)
    {
        var watchRequestId = Guid.NewGuid();
        var matchId = Guid.NewGuid();

        var testEvent = new ProductMatchFound(
            MessageId: Guid.NewGuid(),
            CorrelationId: watchRequestId,
            Timestamp: DateTimeOffset.UtcNow,
            Source: "debug-endpoint",
            MatchId: matchId,
            ProductName: "Test Product - PlayStation 5",
            Price: 449.99m,
            Currency: "USD",
            Seller: "TestSeller");

        await publisher.PublishAsync(testEvent, TopicNames.ProductMatchFound, ct);

        return Results.Ok(new
        {
            message = "Test event published successfully.",
            topicName = TopicNames.ProductMatchFound,
            messageId = testEvent.MessageId,
            correlationId = testEvent.CorrelationId,
            matchId = testEvent.MatchId
        });
    }
}
