using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentPayWatch.Web.Services;

public sealed class ApiClient
{
    private readonly HttpClient _httpClient;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<WatchResponse?> CreateWatchAsync(CreateWatchFormModel model)
    {
        var request = new
        {
            productName = model.ProductName,
            maxPrice = model.MaxPrice,
            currency = model.Currency,
            userId = model.UserId,
            preferredSellers = string.IsNullOrWhiteSpace(model.PreferredSellers)
                ? Array.Empty<string>()
                : model.PreferredSellers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"/api/watches?userId={Uri.EscapeDataString(model.UserId)}", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<WatchResponse>(JsonOptions);
    }

    public async Task<List<WatchResponse>> GetWatchesAsync(string userId)
    {
        var response = await _httpClient.GetFromJsonAsync<List<WatchResponse>>(
            $"/api/watches?userId={Uri.EscapeDataString(userId)}", JsonOptions);
        return response ?? [];
    }

    public async Task<WatchResponse?> GetWatchAsync(Guid id, string userId)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<WatchResponse>(
                $"/api/watches/{id}?userId={Uri.EscapeDataString(userId)}", JsonOptions);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public async Task<List<MatchResponse>> GetMatchesAsync(Guid watchId)
    {
        var response = await _httpClient.GetFromJsonAsync<List<MatchResponse>>(
            $"/api/matches/{watchId}", JsonOptions);
        return response ?? [];
    }

    public async Task<List<TransactionResponse>> GetTransactionsAsync(string userId)
    {
        var response = await _httpClient.GetFromJsonAsync<List<TransactionResponse>>(
            $"/api/transactions?userId={Uri.EscapeDataString(userId)}", JsonOptions);
        return response ?? [];
    }

    public async Task<bool> SubmitApprovalAsync(string token, string decision)
    {
        var request = new { token, decision };
        var response = await _httpClient.PostAsJsonAsync("/api/a2p/callback", request);
        return response.IsSuccessStatusCode;
    }

    public async Task<DashboardData> GetDashboardAsync(string userId)
    {
        var watches = await GetWatchesAsync(userId);
        var transactions = await GetTransactionsAsync(userId);

        return new DashboardData
        {
            ActiveWatchCount = watches.Count(w =>
                w.Status is "Active" or "Matched" or "Paused"),
            PendingApprovalCount = watches.Count(w =>
                w.Status is "AwaitingApproval"),
            CompletedPurchaseCount = watches.Count(w =>
                w.Status is "Completed"),
            RecentWatches = watches
                .OrderByDescending(w => w.UpdatedAt)
                .Take(5)
                .ToList(),
            RecentTransactions = transactions
                .OrderByDescending(t => t.InitiatedAt)
                .Take(5)
                .ToList()
        };
    }
}

public sealed class CreateWatchFormModel
{
    public string ProductName { get; set; } = string.Empty;
    public decimal MaxPrice { get; set; }
    public string Currency { get; set; } = "USD";
    public string? PreferredSellers { get; set; }
    public string UserId { get; set; } = "demo-user";
}

public sealed class WatchResponse
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal MaxPrice { get; set; }
    public string Currency { get; set; } = "USD";
    public string[] PreferredSellers { get; set; } = [];
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string PaymentMethodToken { get; set; } = string.Empty;
    public List<StatusChangeResponse> StatusHistory { get; set; } = [];
}

public sealed class StatusChangeResponse
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; }
    public string? Reason { get; set; }
}

public sealed class MatchResponse
{
    public Guid Id { get; set; }
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string Seller { get; set; } = string.Empty;
    public string ProductUrl { get; set; } = string.Empty;
    public DateTimeOffset MatchedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Availability { get; set; } = string.Empty;
    public string? ApprovalToken { get; set; }
}

public sealed class TransactionResponse
{
    public Guid Id { get; set; }
    public Guid MatchId { get; set; }
    public Guid ApprovalId { get; set; }
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Merchant { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PaymentProviderRef { get; set; } = string.Empty;
    public DateTimeOffset InitiatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
}

public sealed class DashboardData
{
    public int ActiveWatchCount { get; set; }
    public int PendingApprovalCount { get; set; }
    public int CompletedPurchaseCount { get; set; }
    public List<WatchResponse> RecentWatches { get; set; } = [];
    public List<TransactionResponse> RecentTransactions { get; set; } = [];
}
