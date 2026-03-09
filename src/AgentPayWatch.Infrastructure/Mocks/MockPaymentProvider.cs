using System.Collections.Concurrent;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Infrastructure.Mocks;

public sealed class MockPaymentProvider : IPaymentProvider
{
    private readonly ILogger<MockPaymentProvider> _logger;
    private readonly int _successRatePercent;
    private readonly ConcurrentDictionary<string, PaymentResult> _processedPayments = new();
    private readonly Random _random = new();

    private static readonly string[] FailureReasons =
    [
        "Insufficient funds",
        "Card declined",
        "Provider timeout"
    ];

    public MockPaymentProvider(
        ILogger<MockPaymentProvider> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _successRatePercent = configuration.GetValue("Payment:MockSuccessRatePercent", 90);
    }

    public async Task<PaymentResult> ExecutePaymentAsync(
        string idempotencyKey,
        decimal amount,
        string currency,
        string merchant,
        string paymentMethodToken,
        CancellationToken ct)
    {
        if (_processedPayments.TryGetValue(idempotencyKey, out var cachedResult))
        {
            _logger.LogInformation(
                "Idempotent payment request detected for key {IdempotencyKey}. Returning cached result: Success={Success}",
                idempotencyKey,
                cachedResult.Success);
            return cachedResult;
        }

        _logger.LogInformation(
            "Processing payment: {Amount} {Currency} to {Merchant} with token {Token}, idempotency key {IdempotencyKey}",
            amount,
            currency,
            merchant,
            paymentMethodToken,
            idempotencyKey);

        var delayMs = _random.Next(1000, 2001);
        await Task.Delay(delayMs, ct);

        var roll = _random.Next(100);
        PaymentResult result;

        if (roll < _successRatePercent)
        {
            var providerRef = $"PAY-{Guid.NewGuid():N}";
            result = new PaymentResult(
                Success: true,
                ProviderReference: providerRef,
                FailureReason: null);

            _logger.LogInformation(
                "Payment succeeded for key {IdempotencyKey}: {Amount} {Currency} to {Merchant}, ref: {ProviderRef}",
                idempotencyKey,
                amount,
                currency,
                merchant,
                providerRef);
        }
        else
        {
            var failureReason = FailureReasons[_random.Next(FailureReasons.Length)];
            result = new PaymentResult(
                Success: false,
                ProviderReference: null,
                FailureReason: failureReason);

            _logger.LogWarning(
                "Payment failed for key {IdempotencyKey}: {Amount} {Currency} to {Merchant}, reason: {Reason}",
                idempotencyKey,
                amount,
                currency,
                merchant,
                failureReason);
        }

        _processedPayments.TryAdd(idempotencyKey, result);
        return result;
    }
}
