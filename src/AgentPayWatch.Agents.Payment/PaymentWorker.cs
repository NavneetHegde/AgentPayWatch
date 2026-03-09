using System.Text.Json;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Infrastructure.Messaging;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Agents.Payment;

public sealed class PaymentWorker : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly PaymentProcessor _processor;
    private readonly ILogger<PaymentWorker> _logger;

    private ServiceBusProcessor? _sbProcessor;

    public PaymentWorker(
        ServiceBusClient serviceBusClient,
        PaymentProcessor processor,
        ILogger<PaymentWorker> logger)
    {
        _serviceBusClient = serviceBusClient;
        _processor = processor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _sbProcessor = _serviceBusClient.CreateProcessor(
            TopicNames.ApprovalDecided,
            "payment-agent",
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 1,
                PrefetchCount = 0
            });

        _sbProcessor.ProcessMessageAsync += ProcessMessageAsync;
        _sbProcessor.ProcessErrorAsync += ProcessErrorAsync;

        _logger.LogInformation("PaymentWorker starting. Listening on topic '{Topic}', subscription 'payment-agent'",
            TopicNames.ApprovalDecided);

        await _sbProcessor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var body = args.Message.Body.ToString();
        _logger.LogInformation("PaymentWorker received message: {MessageId}", args.Message.MessageId);

        ApprovalDecided? approvalEvent;
        try
        {
            approvalEvent = JsonSerializer.Deserialize<ApprovalDecided>(body, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize ApprovalDecided event from message {MessageId}", args.Message.MessageId);
            await args.CompleteMessageAsync(args.Message);
            return;
        }

        if (approvalEvent is null)
        {
            _logger.LogWarning("Deserialized ApprovalDecided event was null. Message {MessageId}", args.Message.MessageId);
            await args.CompleteMessageAsync(args.Message);
            return;
        }

        if (approvalEvent.Decision != ApprovalDecision.Approved)
        {
            _logger.LogInformation(
                "Skipping non-approved decision '{Decision}' for watch {WatchId}. Message {MessageId}",
                approvalEvent.Decision, approvalEvent.CorrelationId, args.Message.MessageId);
            await args.CompleteMessageAsync(args.Message);
            return;
        }

        try
        {
            await _processor.ProcessAsync(approvalEvent, args.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing payment for watch {WatchId}, match {MatchId}",
                approvalEvent.CorrelationId, approvalEvent.MatchId);
        }

        await args.CompleteMessageAsync(args.Message);
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "PaymentWorker Service Bus error. Source: {ErrorSource}, Entity: {EntityPath}",
            args.ErrorSource, args.FullyQualifiedNamespace);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PaymentWorker stopping");

        if (_sbProcessor is not null)
        {
            await _sbProcessor.StopProcessingAsync(cancellationToken);
            await _sbProcessor.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
