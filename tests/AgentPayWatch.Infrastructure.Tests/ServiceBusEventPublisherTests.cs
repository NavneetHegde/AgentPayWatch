using System.Text.Json;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Infrastructure.Messaging;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentPayWatch.Infrastructure.Tests;

/// <summary>
/// Unit tests for ServiceBusEventPublisher.
/// Uses fake subclasses of the Azure SDK types — no emulator required.
/// The Azure Service Bus SDK explicitly supports this pattern via protected parameterless constructors.
/// </summary>
public sealed class ServiceBusEventPublisherTests : IAsyncDisposable
{
    private readonly FakeServiceBusClient _fakeClient = new();
    private readonly ServiceBusEventPublisher _publisher;

    public ServiceBusEventPublisherTests()
    {
        _publisher = new ServiceBusEventPublisher(_fakeClient, NullLogger<ServiceBusEventPublisher>.Instance);
    }

    // ── Test 1 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_SendsToCorrectTopic()
    {
        var evt = MakeEvent();

        await _publisher.PublishAsync(evt, TopicNames.ProductMatchFound, CancellationToken.None);

        Assert.True(_fakeClient.Senders.ContainsKey(TopicNames.ProductMatchFound));
        Assert.Single(_fakeClient.Senders[TopicNames.ProductMatchFound].SentMessages);
    }

    // ── Test 2 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_SetsMessageIdFromEvent()
    {
        var evt = MakeEvent();

        await _publisher.PublishAsync(evt, TopicNames.ProductMatchFound, CancellationToken.None);

        var sent = _fakeClient.Senders[TopicNames.ProductMatchFound].SentMessages[0];
        Assert.Equal(evt.MessageId.ToString(), sent.MessageId);
    }

    // ── Test 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_SetsCorrelationIdFromEvent()
    {
        var evt = MakeEvent();

        await _publisher.PublishAsync(evt, TopicNames.ProductMatchFound, CancellationToken.None);

        var sent = _fakeClient.Senders[TopicNames.ProductMatchFound].SentMessages[0];
        Assert.Equal(evt.CorrelationId.ToString(), sent.CorrelationId);
    }

    // ── Test 4 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_SetsContentTypeAndSubject()
    {
        var evt = MakeEvent();

        await _publisher.PublishAsync(evt, TopicNames.ProductMatchFound, CancellationToken.None);

        var sent = _fakeClient.Senders[TopicNames.ProductMatchFound].SentMessages[0];
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal(nameof(ProductMatchFound), sent.Subject);
    }

    // ── Test 5 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_BodyIsValidJson()
    {
        var evt = MakeEvent();

        await _publisher.PublishAsync(evt, TopicNames.ProductMatchFound, CancellationToken.None);

        var sent = _fakeClient.Senders[TopicNames.ProductMatchFound].SentMessages[0];
        var json = sent.Body.ToString();

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(evt.ProductName, doc.RootElement.GetProperty("productName").GetString());
        Assert.Equal(evt.Price, doc.RootElement.GetProperty("price").GetDecimal());
        Assert.Equal(evt.Seller, doc.RootElement.GetProperty("seller").GetString());
    }

    // ── Test 6 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_ReusesSenderForSameTopic()
    {
        var evt1 = MakeEvent();
        var evt2 = MakeEvent();

        await _publisher.PublishAsync(evt1, TopicNames.ProductMatchFound, CancellationToken.None);
        await _publisher.PublishAsync(evt2, TopicNames.ProductMatchFound, CancellationToken.None);

        // CreateSender should have been called only once — sender is cached
        Assert.Equal(1, _fakeClient.CreateSenderCallCount);
        Assert.Equal(2, _fakeClient.Senders[TopicNames.ProductMatchFound].SentMessages.Count);
    }

    // ── Test 7 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_CreatesSeparateSendersForDifferentTopics()
    {
        var matchEvt = MakeEvent();
        var approvalEvt = new ApprovalDecided(
            MessageId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Source: "test",
            ApprovalId: Guid.NewGuid(),
            MatchId: Guid.NewGuid(),
            Decision: Domain.Enums.ApprovalDecision.Approved);

        await _publisher.PublishAsync(matchEvt, TopicNames.ProductMatchFound, CancellationToken.None);
        await _publisher.PublishAsync(approvalEvt, TopicNames.ApprovalDecided, CancellationToken.None);

        Assert.Equal(2, _fakeClient.CreateSenderCallCount);
        Assert.True(_fakeClient.Senders.ContainsKey(TopicNames.ProductMatchFound));
        Assert.True(_fakeClient.Senders.ContainsKey(TopicNames.ApprovalDecided));
    }

    // ── Test 8 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_ThrowsOnNullMessage()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _publisher.PublishAsync<ProductMatchFound>(null!, TopicNames.ProductMatchFound, CancellationToken.None));
    }

    // ── Test 9 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_ThrowsOnEmptyTopicName()
    {
        var evt = MakeEvent();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _publisher.PublishAsync(evt, string.Empty, CancellationToken.None));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ProductMatchFound MakeEvent() => new(
        MessageId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        Timestamp: DateTimeOffset.UtcNow,
        Source: "unit-test",
        MatchId: Guid.NewGuid(),
        ProductName: "PlayStation 5",
        Price: 449.99m,
        Currency: "USD",
        Seller: "TestSeller");

    public async ValueTask DisposeAsync() => await _publisher.DisposeAsync();

    // ── Azure SDK Fakes (designed for subclassing via protected ctor) ──────────

    private sealed class FakeServiceBusSender : ServiceBusSender
    {
        public List<ServiceBusMessage> SentMessages { get; } = [];

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServiceBusClient : ServiceBusClient
    {
        private readonly Dictionary<string, FakeServiceBusSender> _senders = new();

        public IReadOnlyDictionary<string, FakeServiceBusSender> Senders => _senders;
        public int CreateSenderCallCount { get; private set; }

        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            CreateSenderCallCount++;
            var sender = new FakeServiceBusSender();
            _senders[queueOrTopicName] = sender;
            return sender;
        }
    }
}
