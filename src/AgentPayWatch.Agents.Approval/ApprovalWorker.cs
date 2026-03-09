using System.Security.Cryptography;
using System.Text.Json;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Messaging;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Agents.Approval;

public sealed class ApprovalWorker : BackgroundService, IAsyncDisposable
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IApprovalRepository _approvalRepo;
    private readonly IProductMatchRepository _matchRepo;
    private readonly IWatchRequestRepository _watchRepo;
    private readonly IA2PClient _a2pClient;
    private readonly ILogger<ApprovalWorker> _logger;

    private ServiceBusProcessor? _processor;

    public ApprovalWorker(
        ServiceBusClient serviceBusClient,
        IApprovalRepository approvalRepo,
        IProductMatchRepository matchRepo,
        IWatchRequestRepository watchRepo,
        IA2PClient a2pClient,
        ILogger<ApprovalWorker> logger)
    {
        _serviceBusClient = serviceBusClient;
        _approvalRepo = approvalRepo;
        _matchRepo = matchRepo;
        _watchRepo = watchRepo;
        _a2pClient = a2pClient;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor = _serviceBusClient.CreateProcessor(
            TopicNames.ProductMatchFound,
            "sub-approval-agent",
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 1
            });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        _logger.LogInformation(
            "ApprovalWorker starting. Listening on topic '{Topic}', subscription '{Subscription}'.",
            TopicNames.ProductMatchFound,
            "sub-approval-agent");

        await _processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown
        }

        _logger.LogInformation("ApprovalWorker stopping...");
        await _processor.StopProcessingAsync(CancellationToken.None);
        _logger.LogInformation("ApprovalWorker stopped.");
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        ProductMatchFound? matchEvent = null;
        try
        {
            matchEvent = JsonSerializer.Deserialize<ProductMatchFound>(
                args.Message.Body.ToString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (matchEvent is null)
            {
                _logger.LogWarning("Received null or undeserializable message. Completing and skipping.");
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                return;
            }

            _logger.LogInformation(
                "Processing ProductMatchFound event. MatchId: {MatchId}, Product: {ProductName}, Price: {Price}",
                matchEvent.MatchId,
                matchEvent.ProductName,
                matchEvent.Price);

            // Load the ProductMatch from Cosmos
            // CorrelationId is set to the WatchRequestId by the ProductWatchWorker
            ProductMatch? productMatch = await _matchRepo.GetByIdAsync(
                matchEvent.MatchId,
                matchEvent.CorrelationId,
                args.CancellationToken);
            if (productMatch is null)
            {
                _logger.LogWarning(
                    "ProductMatch {MatchId} not found in Cosmos. Completing message.",
                    matchEvent.MatchId);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                return;
            }

            // Load the WatchRequest from Cosmos
            WatchRequest? watch = await _watchRepo.GetByIdAsync(
                productMatch.WatchRequestId,
                productMatch.UserId,
                args.CancellationToken);
            if (watch is null)
            {
                _logger.LogWarning(
                    "WatchRequest {WatchRequestId} not found for match {MatchId}. Completing message.",
                    productMatch.WatchRequestId,
                    matchEvent.MatchId);
                await args.CompleteMessageAsync(args.Message, args.CancellationToken);
                return;
            }

            // Generate a cryptographically secure approval token (URL-safe base64)
            string approvalToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset expiresAt = now.AddMinutes(15);

            // Create the ApprovalRecord
            var approval = new ApprovalRecord
            {
                Id = Guid.NewGuid(),
                MatchId = productMatch.Id,
                WatchRequestId = watch.Id,
                UserId = watch.UserId,
                ApprovalToken = approvalToken,
                SentAt = now,
                ExpiresAt = expiresAt,
                RespondedAt = null,
                Decision = ApprovalDecision.Pending,
                Channel = watch.NotificationChannel
            };

            // Save approval to Cosmos
            await _approvalRepo.CreateAsync(approval, args.CancellationToken);

            // Update watch status to AwaitingApproval
            watch.UpdateStatus(WatchStatus.AwaitingApproval);
            await _watchRepo.UpdateAsync(watch, args.CancellationToken);

            // Send the A2P approval request message
            await _a2pClient.SendApprovalRequestAsync(
                watch.PhoneNumber,
                productMatch.ProductName,
                productMatch.Price,
                productMatch.Seller,
                approvalToken,
                args.CancellationToken);

            _logger.LogInformation(
                "Approval requested for watch {WatchId}, token: {Token}, expires: {ExpiresAt}",
                watch.Id,
                approvalToken,
                expiresAt);

            // Complete the Service Bus message
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error processing ProductMatchFound message for match {MatchId}. Message will be retried.",
                matchEvent?.MatchId);

            // Abandon the message so Service Bus redelivers it
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(
            args.Exception,
            "Service Bus processor error. Source: {Source}, Namespace: {Namespace}, EntityPath: {EntityPath}",
            args.ErrorSource,
            args.FullyQualifiedNamespace,
            args.EntityPath);

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null)
        {
            await _processor.DisposeAsync();
        }
    }
}
