using AgentPayWatch.Domain.Models;

namespace AgentPayWatch.Domain.Interfaces;

public interface IProductSource
{
    Task<IReadOnlyList<ProductListing>> SearchAsync(string productName, CancellationToken ct);
}
