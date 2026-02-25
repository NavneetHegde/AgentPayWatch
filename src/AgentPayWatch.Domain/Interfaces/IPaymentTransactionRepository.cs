using AgentPayWatch.Domain.Entities;

namespace AgentPayWatch.Domain.Interfaces;

public interface IPaymentTransactionRepository
{
    Task<PaymentTransaction> CreateAsync(PaymentTransaction transaction, CancellationToken ct = default);
    Task<PaymentTransaction?> GetByIdAsync(Guid id, string userId, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentTransaction>> GetByUserIdAsync(string userId, CancellationToken ct = default);
    Task UpdateAsync(PaymentTransaction transaction, CancellationToken ct = default);
}
