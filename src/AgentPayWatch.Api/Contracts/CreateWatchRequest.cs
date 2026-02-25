namespace AgentPayWatch.Api.Contracts;

public sealed record CreateWatchRequest(
    string ProductName,
    decimal MaxPrice,
    string Currency = "USD",
    string[]? PreferredSellers = null);
