using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentPayWatch.Domain.Interfaces;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Infrastructure.Messaging;

public sealed class ServiceBusEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusEventPublisher> _logger;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ServiceBusEventPublisher(
        ServiceBusClient client,
        ILogger<ServiceBusEventPublisher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task PublishAsync<T>(T message, string topicName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);

        var sender = _senders.GetOrAdd(topicName, name => _client.CreateSender(name));

        var json = JsonSerializer.Serialize(message, JsonOptions);

        var serviceBusMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            Subject = typeof(T).Name
        };

        // Extract MessageId and CorrelationId from the message if they exist
        if (message is not null)
        {
            var messageIdProperty = typeof(T).GetProperty("MessageId");
            if (messageIdProperty?.GetValue(message) is Guid messageId)
            {
                serviceBusMessage.MessageId = messageId.ToString();
            }

            var correlationIdProperty = typeof(T).GetProperty("CorrelationId");
            if (correlationIdProperty?.GetValue(message) is Guid correlationId)
            {
                serviceBusMessage.CorrelationId = correlationId.ToString();
            }
        }

        _logger.LogInformation(
            "Publishing {EventType} to topic '{TopicName}'. MessageId={MessageId}, CorrelationId={CorrelationId}",
            typeof(T).Name,
            topicName,
            serviceBusMessage.MessageId,
            serviceBusMessage.CorrelationId);

        await sender.SendMessageAsync(serviceBusMessage, ct);

        _logger.LogInformation(
            "Successfully published {EventType} to topic '{TopicName}'.",
            typeof(T).Name,
            topicName);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync();
        }

        _senders.Clear();
    }
}
