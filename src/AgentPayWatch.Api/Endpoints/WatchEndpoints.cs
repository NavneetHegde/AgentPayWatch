using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace AgentPayWatch.Api.Endpoints;

public static class WatchEndpoints
{
    public static IEndpointRouteBuilder MapWatchEndpoints(this IEndpointRouteBuilder routes)
    {

        routes.MapGet("/", () => "AgentPay Watch Api")
            .WithTags("Root");

        var group = routes.MapGroup("/api/watches")
            .WithTags("Watches");

        group.MapPost("/", CreateWatch)
            .WithName("CreateWatch")
            .WithDescription("Create a new watch request.");

        group.MapGet("/", ListWatches)
            .WithName("ListWatches")
            .WithDescription("List watch requests for a user.");

        group.MapGet("/{id:guid}", GetWatch)
            .WithName("GetWatch")
            .WithDescription("Get a single watch request by ID.");

        group.MapPut("/{id:guid}/pause", PauseWatch)
            .WithName("PauseWatch")
            .WithDescription("Pause an active watch.");

        group.MapPut("/{id:guid}/resume", ResumeWatch)
            .WithName("ResumeWatch")
            .WithDescription("Resume a paused watch.");

        group.MapDelete("/{id:guid}", CancelWatch)
            .WithName("CancelWatch")
            .WithDescription("Cancel a watch request.");

        return routes;
    }

    private static async Task<Results<Created<WatchResponse>, BadRequest<string>>> CreateWatch(
        CreateWatchRequest request,
        [FromQuery] string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(request.ProductName))
            return TypedResults.BadRequest("ProductName is required.");

        if (request.MaxPrice <= 0)
            return TypedResults.BadRequest("MaxPrice must be greater than zero.");

        if (string.IsNullOrWhiteSpace(userId))
            return TypedResults.BadRequest("userId query parameter is required.");

        var entity = new WatchRequest
        {
            UserId = userId,
            ProductName = request.ProductName,
            MaxPrice = request.MaxPrice,
            Currency = request.Currency,
            PreferredSellers = request.PreferredSellers ?? []
        };

        var created = await repository.CreateAsync(entity);
        var response = WatchResponse.FromEntity(created);

        return TypedResults.Created($"/api/watches/{response.Id}", response);
    }

    private static async Task<Results<Ok<IReadOnlyList<WatchResponse>>, BadRequest<string>>> ListWatches(
        [FromQuery] string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return TypedResults.BadRequest("userId query parameter is required.");

        var entities = await repository.GetByUserIdAsync(userId);
        var responses = entities.Select(WatchResponse.FromEntity).ToList() as IReadOnlyList<WatchResponse>;

        return TypedResults.Ok(responses);
    }

    private static async Task<Results<Ok<WatchResponse>, BadRequest<string>, NotFound>> GetWatch(
        Guid id,
        [FromQuery] string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return TypedResults.BadRequest("userId query parameter is required.");

        var entity = await repository.GetByIdAsync(id, userId);
        if (entity is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(WatchResponse.FromEntity(entity));
    }

    private static async Task<Results<Ok<WatchResponse>, BadRequest<string>, NotFound>> PauseWatch(
        Guid id,
        [FromQuery] string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return TypedResults.BadRequest("userId query parameter is required.");

        var entity = await repository.GetByIdAsync(id, userId);
        if (entity is null)
            return TypedResults.NotFound();

        try { entity.UpdateStatus(WatchStatus.Paused, "User requested pause."); }
        catch (InvalidOperationException ex) { return TypedResults.BadRequest(ex.Message); }

        await repository.UpdateAsync(entity);

        return TypedResults.Ok(WatchResponse.FromEntity(entity));
    }

    private static async Task<Results<Ok<WatchResponse>, BadRequest<string>, NotFound>> ResumeWatch(
        Guid id,
        [FromQuery] string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return TypedResults.BadRequest("userId query parameter is required.");

        var entity = await repository.GetByIdAsync(id, userId);
        if (entity is null)
            return TypedResults.NotFound();

        try { entity.UpdateStatus(WatchStatus.Active, "User requested resume."); }
        catch (InvalidOperationException ex) { return TypedResults.BadRequest(ex.Message); }

        await repository.UpdateAsync(entity);

        return TypedResults.Ok(WatchResponse.FromEntity(entity));
    }

    private static async Task<Results<NoContent, BadRequest<string>, NotFound>> CancelWatch(
        Guid id,
        [FromQuery] string userId,
        IWatchRequestRepository repository)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return TypedResults.BadRequest("userId query parameter is required.");

        var entity = await repository.GetByIdAsync(id, userId);
        if (entity is null)
            return TypedResults.NotFound();

        try { entity.UpdateStatus(WatchStatus.Cancelled, "User requested cancellation."); }
        catch (InvalidOperationException ex) { return TypedResults.BadRequest(ex.Message); }

        await repository.UpdateAsync(entity);

        return TypedResults.NoContent();
    }
}
