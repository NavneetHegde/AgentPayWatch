using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Models;

public sealed record ProductListing(
    string Name,
    decimal Price,
    string Currency,
    string Seller,
    string Url,
    ProductAvailability Availability);
