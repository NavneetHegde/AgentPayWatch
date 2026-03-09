using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Models;

namespace AgentPayWatch.Agents.ProductWatch;

public static class MatchingService
{
    /// <summary>
    /// Determines whether a product listing satisfies the watch request criteria.
    /// A listing is a match if:
    ///   1. Its price is at or below the watch's max price, AND
    ///   2. The watch has no preferred sellers, OR the listing's seller is in the preferred list.
    /// </summary>
    public static bool IsMatch(WatchRequest watch, ProductListing listing)
    {
        if (listing.Price > watch.MaxPrice)
        {
            return false;
        }

        if (watch.PreferredSellers is null || watch.PreferredSellers.Length == 0)
        {
            return true;
        }

        return watch.PreferredSellers.Any(
            seller => string.Equals(seller, listing.Seller, StringComparison.OrdinalIgnoreCase)
        );
    }
}
