using AgentPayWatch.Domain.Entities;

namespace AgentPayWatch.Domain.Interfaces;

public interface IProductMatchRepository
{
    Task<ProductMatch> CreateAsync(ProductMatch match, CancellationToken ct = default);
    Task<ProductMatch?> GetByIdAsync(Guid id, Guid watchRequestId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductMatch>> GetByWatchRequestIdAsync(Guid watchRequestId, CancellationToken ct = default);
}
