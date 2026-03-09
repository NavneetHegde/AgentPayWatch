using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentPayWatch.Api.Endpoints;

public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/transactions");

        group.MapGet("/", async (
            string userId,
            IPaymentTransactionRepository transactionRepo) =>
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.BadRequest(new { error = "userId query parameter is required." });
            }

            var transactions = await transactionRepo.GetByUserIdAsync(userId);

            var response = transactions
                .OrderByDescending(t => t.InitiatedAt)
                .Select(TransactionResponse.FromEntity)
                .ToList();

            return Results.Ok(response);
        })
        .WithName("GetTransactions")
        .WithOpenApi();

        group.MapGet("/{id:guid}", async (
            Guid id,
            string userId,
            IPaymentTransactionRepository transactionRepo) =>
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.BadRequest(new { error = "userId query parameter is required." });
            }

            var transaction = await transactionRepo.GetByIdAsync(id, userId);

            if (transaction is null)
            {
                return Results.NotFound(new { error = $"Transaction {id} not found." });
            }

            return Results.Ok(TransactionResponse.FromEntity(transaction));
        })
        .WithName("GetTransaction")
        .WithOpenApi();

        return routes;
    }
}
