using System.Text.Json;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Infrastructure.Messaging;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPayWatch.Infrastructure.Tests;

/// <summary>
/// Integration tests for ServiceBusEventPublisher against the live Service Bus emulator.
/// Requires Aspire to be running: aspire run .\appHost\apphost.cs
///
/// Tests are automatically skipped if the emulator is unreachable.
/// </summary>
[Collection("ServiceBusIntegration")]
public sealed class ServiceBusEventPublisherIntegrationTests : IAsyncLifetime
{
    private readonly ServiceBusFixture _fixture;
    private ServiceBusEventPublisher _publisher = null!;

    public ServiceBusEventPublisherIntegrationTests(ServiceBusFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        Skip.If(!_fixture.IsAvailable,
            $"Service Bus emulator unreachable — start Aspire first ('aspire run .\\appHost\\apphost.cs'). " +
            $"Reason: {_fixture.UnavailableReason}");

        _publisher = new ServiceBusEventPublisher(_fixture.Client!, NullLogger<ServiceBusEventPublisher>.Instance);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _publisher.DisposeAsync();

    // ── Test 1 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task PublishAsync_ProductMatchFound_MessageArrivesOnTopic()
    {
        var evt = new ProductMatchFound(
            MessageId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Source: "integration-test",
            MatchId: Guid.NewGuid(),
            ProductName: "Integration Test Product",
            Price: 299.99m,
            Currency: "USD",
            Seller: "TestSeller");

        // Publish
        await _publisher.PublishAsync(evt, TopicNames.ProductMatchFound, CancellationToken.None);

        // Peek (non-destructive) from the subscription to verify delivery
        await using var receiver = _fixture.Client!.CreateReceiver(
            TopicNames.ProductMatchFound,
            "sub-approval-agent",
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peeked = await PeekUntilFoundAsync(receiver, evt.MessageId.ToString(), cts.Token);

        Assert.NotNull(peeked);
        Assert.Equal(evt.MessageId.ToString(), peeked.MessageId);
        Assert.Equal(evt.CorrelationId.ToString(), peeked.CorrelationId);
        Assert.Equal("application/json", peeked.ContentType);
        Assert.Equal(nameof(ProductMatchFound), peeked.Subject);

        // Verify body deserializes correctly
        var body = JsonDocument.Parse(peeked.Body.ToString());
        Assert.Equal(evt.ProductName, body.RootElement.GetProperty("productName").GetString());
        Assert.Equal(evt.Price, body.RootElement.GetProperty("price").GetDecimal());
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task PublishAsync_AllTopics_ReachableWithoutError()
    {
        // Smoke test: just verify we can publish to all four topics without exception.
        var matchId = Guid.NewGuid();

        await _publisher.PublishAsync(
            new ProductMatchFound(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "smoke-test",
                matchId, "Smoke Product", 1.00m, "USD", "Seller"),
            TopicNames.ProductMatchFound, CancellationToken.None);

        await _publisher.PublishAsync(
            new ApprovalDecided(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "smoke-test",
                Guid.NewGuid(), matchId, Domain.Enums.ApprovalDecision.Approved),
            TopicNames.ApprovalDecided, CancellationToken.None);

        await _publisher.PublishAsync(
            new PaymentCompleted(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "smoke-test",
                Guid.NewGuid(), 1.00m, "USD", "TestMerchant"),
            TopicNames.PaymentCompleted, CancellationToken.None);

        await _publisher.PublishAsync(
            new PaymentFailed(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "smoke-test",
                Guid.NewGuid(), "Insufficient funds"),
            TopicNames.PaymentFailed, CancellationToken.None);

        // No exception = all four topics are reachable
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Peeks messages in batches until the one with the given messageId is found.
    /// Service Bus peek returns up to 250 messages; loop until found or timeout.
    /// </summary>
    private static async Task<ServiceBusReceivedMessage?> PeekUntilFoundAsync(
        ServiceBusReceiver receiver,
        string messageId,
        CancellationToken ct)
    {
        long fromSeq = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await receiver.PeekMessagesAsync(maxMessages: 50, fromSequenceNumber: fromSeq, cancellationToken: ct);
            if (batch.Count == 0) break;

            foreach (var msg in batch)
            {
                if (msg.MessageId == messageId) return msg;
                if (msg.SequenceNumber > fromSeq) fromSeq = msg.SequenceNumber;
            }

            fromSeq++; // advance past last seen
        }
        return null;
    }
}
