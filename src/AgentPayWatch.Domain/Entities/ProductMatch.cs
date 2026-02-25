using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Entities;

public sealed class ProductMatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WatchRequestId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string Seller { get; set; } = string.Empty;
    public string ProductUrl { get; set; } = string.Empty;
    public DateTimeOffset MatchedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public ProductAvailability Availability { get; set; } = ProductAvailability.InStock;
}
