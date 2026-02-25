using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Domain.Interfaces;

public interface IWatchRequestRepository
{
    Task<WatchRequest> CreateAsync(WatchRequest watchRequest, CancellationToken ct = default);
    Task<WatchRequest?> GetByIdAsync(Guid id, string? userId = null, CancellationToken ct = default);
    Task<IReadOnlyList<WatchRequest>> GetByUserIdAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<WatchRequest>> GetByStatusAsync(WatchStatus status, CancellationToken ct = default);
    Task UpdateAsync(WatchRequest watchRequest, CancellationToken ct = default);
}
