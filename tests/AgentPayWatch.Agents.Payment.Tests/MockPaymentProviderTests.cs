using AgentPayWatch.Infrastructure.Mocks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPayWatch.Agents.Payment.Tests;

public sealed class MockPaymentProviderTests
{
    // NOTE: MockPaymentProvider includes a 1-2s simulated delay per call.
    // Tests are kept to the absolute minimum number of calls to stay fast.

    // ── Success result shape ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecutePaymentAsync_SuccessResult_HasProviderRefAndNoFailureReason()
    {
        var provider = BuildProvider(successRate: 100);

        var result = await provider.ExecutePaymentAsync(
            "key-1", 249.99m, "USD", "TechMart", "tok_demo", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.ProviderReference);
        Assert.StartsWith("PAY-", result.ProviderReference);
        Assert.Null(result.FailureReason);
    }

    // ── Failure result shape ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecutePaymentAsync_FailureResult_HasFailureReasonAndNoProviderRef()
    {
        var provider = BuildProvider(successRate: 0);

        var result = await provider.ExecutePaymentAsync(
            "key-1", 249.99m, "USD", "TechMart", "tok_demo", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.ProviderReference);
        Assert.NotNull(result.FailureReason);
        Assert.NotEmpty(result.FailureReason);
    }

    [Fact]
    public async Task ExecutePaymentAsync_FailureReason_IsOneOfKnownValues()
    {
        var provider = BuildProvider(successRate: 0);
        string[] knownReasons = ["Insufficient funds", "Card declined", "Provider timeout"];

        // 3 calls — enough to verify the reason is always drawn from the known set
        for (var i = 0; i < 3; i++)
        {
            var result = await provider.ExecutePaymentAsync(
                Guid.NewGuid().ToString(), 100m, "USD", "Shop", "tok", CancellationToken.None);

            Assert.Contains(result.FailureReason!, knownReasons);
        }
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecutePaymentAsync_ReturnsSameResult_ForDuplicateKey()
    {
        var provider = BuildProvider(successRate: 100);
        const string key = "idempotent-key-abc";

        var first = await provider.ExecutePaymentAsync(key, 100m, "USD", "Shop", "tok", CancellationToken.None);
        var second = await provider.ExecutePaymentAsync(key, 100m, "USD", "Shop", "tok", CancellationToken.None);

        Assert.Equal(first.Success, second.Success);
        Assert.Equal(first.ProviderReference, second.ProviderReference);
        Assert.Equal(first.FailureReason, second.FailureReason);
    }

    [Fact]
    public async Task ExecutePaymentAsync_IdempotencyKey_CanPreserveFailure()
    {
        var provider = BuildProvider(successRate: 0);
        const string key = "idempotent-failure-key";

        var first = await provider.ExecutePaymentAsync(key, 100m, "USD", "Shop", "tok", CancellationToken.None);
        var second = await provider.ExecutePaymentAsync(key, 100m, "USD", "Shop", "tok", CancellationToken.None);

        Assert.False(first.Success);
        Assert.Equal(first.FailureReason, second.FailureReason);
    }

    [Fact]
    public async Task ExecutePaymentAsync_DifferentKeys_ProduceDifferentProviderRefs()
    {
        var provider = BuildProvider(successRate: 100);

        var result1 = await provider.ExecutePaymentAsync("key-a", 100m, "USD", "Shop", "tok", CancellationToken.None);
        var result2 = await provider.ExecutePaymentAsync("key-b", 100m, "USD", "Shop", "tok", CancellationToken.None);

        Assert.True(result1.Success);
        Assert.True(result2.Success);
        Assert.NotEqual(result1.ProviderReference, result2.ProviderReference);
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecutePaymentAsync_ReturnsValidResult_WhenNoConfigProvided()
    {
        // Default is 90% success — just verify the result structure is valid
        var provider = new MockPaymentProvider(
            NullLogger<MockPaymentProvider>.Instance,
            new ConfigurationBuilder().Build());

        var result = await provider.ExecutePaymentAsync(
            "key-default", 100m, "USD", "Shop", "tok", CancellationToken.None);

        // Result must be either a clean success or a clean failure — never ambiguous
        if (result.Success)
        {
            Assert.NotNull(result.ProviderReference);
            Assert.Null(result.FailureReason);
        }
        else
        {
            Assert.Null(result.ProviderReference);
            Assert.NotNull(result.FailureReason);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MockPaymentProvider BuildProvider(int successRate)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payment:MockSuccessRatePercent"] = successRate.ToString()
            })
            .Build();

        return new MockPaymentProvider(NullLogger<MockPaymentProvider>.Instance, config);
    }
}
