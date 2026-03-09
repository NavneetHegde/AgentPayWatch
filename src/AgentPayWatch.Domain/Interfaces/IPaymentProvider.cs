namespace AgentPayWatch.Domain.Interfaces;

public record PaymentResult(
    bool Success,
    string? ProviderReference,
    string? FailureReason);

public interface IPaymentProvider
{
    Task<PaymentResult> ExecutePaymentAsync(
        string idempotencyKey,
        decimal amount,
        string currency,
        string merchant,
        string paymentMethodToken,
        CancellationToken ct);
}
