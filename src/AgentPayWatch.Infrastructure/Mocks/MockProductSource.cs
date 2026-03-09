using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Domain.Models;

namespace AgentPayWatch.Infrastructure.Mocks;

public sealed class MockProductSource : IProductSource
{
    private static readonly List<CatalogEntry> Catalog =
    [
        // Electronics
        new("iPhone 15 Pro", 949.00m, "USD", "TechZone", ProductAvailability.InStock),
        new("iPhone 15 Pro Max", 1099.00m, "USD", "MegaDeals", ProductAvailability.InStock),
        new("Samsung Galaxy S24 Ultra", 879.00m, "USD", "GadgetWorld", ProductAvailability.LimitedStock),
        new("Sony WH-1000XM5 Headphones", 298.00m, "USD", "AudioHaven", ProductAvailability.InStock),

        // Books
        new("Clean Architecture Book", 32.00m, "USD", "BookNest", ProductAvailability.InStock),
        new("Designing Data-Intensive Applications", 38.00m, "USD", "PageTurner", ProductAvailability.InStock),

        // Gaming
        new("PlayStation 5 Console", 449.00m, "USD", "GameVault", ProductAvailability.LimitedStock),
        new("Xbox Series X", 429.00m, "USD", "TechZone", ProductAvailability.InStock),
        new("Nintendo Switch OLED", 299.00m, "USD", "GameVault", ProductAvailability.InStock),

        // Kitchen
        new("KitchenAid Stand Mixer", 329.00m, "USD", "HomeEssentials", ProductAvailability.InStock),
    ];

    public Task<IReadOnlyList<ProductListing>> SearchAsync(string productName, CancellationToken ct)
    {
        var results = new List<ProductListing>();
        var searchTerm = productName.Trim();

        foreach (var entry in Catalog)
        {
            if (!entry.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Seed from product name hash + current minute so results vary over time
            // but are deterministic within the same minute for the same product.
            int seed = HashCode.Combine(entry.Name, DateTime.UtcNow.Minute);
            var rng = new Random(seed);

            // About 70% chance we return this product at all (simulates availability)
            if (rng.NextDouble() > 0.70)
            {
                continue;
            }

            // Apply +/- 15% price jitter to the base price
            double jitterFactor = 1.0 + ((rng.NextDouble() * 0.30) - 0.15); // range: 0.85 to 1.15
            decimal jitteredPrice = Math.Round(entry.BasePrice * (decimal)jitterFactor, 2);

            string slug = entry.Name.ToLowerInvariant()
                .Replace(' ', '-')
                .Replace(".", "");

            var listing = new ProductListing(
                Name: entry.Name,
                Price: jitteredPrice,
                Currency: entry.Currency,
                Seller: entry.Seller,
                Url: $"https://store.example.com/products/{slug}",
                Availability: entry.Availability
            );

            results.Add(listing);
        }

        // Cap at 3 results maximum
        if (results.Count > 3)
        {
            results = results.Take(3).ToList();
        }

        return Task.FromResult<IReadOnlyList<ProductListing>>(results);
    }

    private sealed record CatalogEntry(
        string Name,
        decimal BasePrice,
        string Currency,
        string Seller,
        ProductAvailability Availability
    );
}
