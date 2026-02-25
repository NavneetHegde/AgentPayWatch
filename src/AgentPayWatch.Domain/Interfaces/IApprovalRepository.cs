using AgentPayWatch.Domain.Entities;

namespace AgentPayWatch.Domain.Interfaces;

public interface IApprovalRepository
{
    Task<ApprovalRecord> CreateAsync(ApprovalRecord approval, CancellationToken ct = default);
    Task<ApprovalRecord?> GetByIdAsync(Guid id, Guid watchRequestId, CancellationToken ct = default);
    Task<ApprovalRecord?> GetByTokenAsync(string token, CancellationToken ct = default);
    Task<ApprovalRecord?> GetByMatchIdAsync(Guid matchId, Guid watchRequestId, CancellationToken ct = default);
    Task UpdateAsync(ApprovalRecord approval, CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalRecord>> GetPendingExpiredAsync(DateTimeOffset now, CancellationToken ct = default);
}
