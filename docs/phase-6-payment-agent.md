# Phase 6: Payment Agent -- Transaction Execution

**Goal:** Approved matches trigger mock payments. Full end-to-end event flow completes. Testable entirely via curl.

**Prerequisite:** Phase 5 complete (Approval flow working -- watches transition through Active, Matched, AwaitingApproval, Approved via curl).

---

## Section 1: Mock Payment Provider

### 1.1 Payment Provider Interface

**File:** `src/AgentPayWatch.Domain/Interfaces/IPaymentProvider.cs`

```csharp
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
```

### 1.2 Mock Payment Provider Implementation

**File:** `src/AgentPayWatch.Infrastructure/Mocks/MockPaymentProvider.cs`

```csharp
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
```

### 1.3 Register in DependencyInjection.cs

**File:** `src/AgentPayWatch.Infrastructure/DependencyInjection.cs`

Add the following registration inside the `AddInfrastructureServices` method, alongside the existing registrations for repositories, event publisher, product source, and A2P client:

```csharp
using AgentPayWatch.Infrastructure.Mocks;

// ... inside AddInfrastructureServices method, add:
services.AddSingleton<IPaymentProvider, MockPaymentProvider>();
```

The full updated `DependencyInjection.cs` should contain all previous registrations plus the new one. Here is the complete file for reference:

```csharp
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Cosmos;
using AgentPayWatch.Infrastructure.Messaging;
using AgentPayWatch.Infrastructure.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentPayWatch.Infrastructure;

public static class DependencyInjection
{
    public static TBuilder AddInfrastructureServices<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddAzureCosmosClient("cosmos");
        builder.AddAzureServiceBusClient("messaging");

        builder.Services.AddHostedService<CosmosDbInitializer>();

        builder.Services.AddSingleton<IWatchRequestRepository, CosmosWatchRequestRepository>();
        builder.Services.AddSingleton<IProductMatchRepository, CosmosProductMatchRepository>();
        builder.Services.AddSingleton<IApprovalRepository, CosmosApprovalRepository>();
        builder.Services.AddSingleton<IPaymentTransactionRepository, CosmosPaymentTransactionRepository>();

        builder.Services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();
        builder.Services.AddSingleton<IProductSource, MockProductSource>();
        builder.Services.AddSingleton<IA2PClient, MockA2PClient>();
        builder.Services.AddSingleton<IPaymentProvider, MockPaymentProvider>();

        return builder;
    }
}
```

---

## Section 2: Payment Worker

**File:** `src/AgentPayWatch.Agents.Payment/PaymentWorker.cs`

```csharp
using System.Text.Json;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Messaging;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Agents.Payment;

public sealed class PaymentWorker : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly IWatchRequestRepository _watchRepo;
    private readonly IProductMatchRepository _matchRepo;
    private readonly IPaymentTransactionRepository _transactionRepo;
    private readonly IPaymentProvider _paymentProvider;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<PaymentWorker> _logger;

    private ServiceBusProcessor? _processor;

    public PaymentWorker(
        ServiceBusClient serviceBusClient,
        IWatchRequestRepository watchRepo,
        IProductMatchRepository matchRepo,
        IPaymentTransactionRepository transactionRepo,
        IPaymentProvider paymentProvider,
        IEventPublisher eventPublisher,
        ILogger<PaymentWorker> logger)
    {
        _serviceBusClient = serviceBusClient;
        _watchRepo = watchRepo;
        _matchRepo = matchRepo;
        _transactionRepo = transactionRepo;
        _paymentProvider = paymentProvider;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor = _serviceBusClient.CreateProcessor(
            TopicNames.ApprovalDecided,
            "payment-agent",
            new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 1,
                PrefetchCount = 0
            });

        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        _logger.LogInformation("PaymentWorker starting. Listening on topic '{Topic}', subscription 'payment-agent'",
            TopicNames.ApprovalDecided);

        await _processor.StartProcessingAsync(stoppingToken);

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
                approvalEvent.Decision,
                approvalEvent.CorrelationId,
                args.Message.MessageId);
            await args.CompleteMessageAsync(args.Message);
            return;
        }

        try
        {
            await ProcessApprovedPaymentAsync(approvalEvent, args.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing payment for watch {WatchId}, match {MatchId}",
                approvalEvent.CorrelationId,
                approvalEvent.MatchId);
        }

        await args.CompleteMessageAsync(args.Message);
    }

    private async Task ProcessApprovedPaymentAsync(ApprovalDecided approvalEvent, CancellationToken ct)
    {
        var watchRequestId = approvalEvent.CorrelationId;
        var matchId = approvalEvent.MatchId;
        var approvalId = approvalEvent.ApprovalId;

        _logger.LogInformation(
            "Processing approved payment for watch {WatchId}, match {MatchId}, approval {ApprovalId}",
            watchRequestId,
            matchId,
            approvalId);

        // Load watch request -- we need to find it by querying by status or we need the userId.
        // The CorrelationId is the WatchRequestId. We need the userId for the partition key.
        // We'll use GetByStatusAsync to find watches in Approved status, then match by Id.
        var approvedWatches = await _watchRepo.GetByStatusAsync(WatchStatus.Approved);
        var watch = approvedWatches.FirstOrDefault(w => w.Id == watchRequestId);

        if (watch is null)
        {
            _logger.LogWarning(
                "Watch request {WatchId} not found in Approved status. It may have already been processed.",
                watchRequestId);
            return;
        }

        // Load the product match
        var match = await _matchRepo.GetByIdAsync(matchId, watchRequestId);
        if (match is null)
        {
            _logger.LogWarning(
                "Product match {MatchId} not found for watch {WatchId}",
                matchId,
                watchRequestId);
            return;
        }

        // Transition watch to Purchasing
        watch.UpdateStatus(WatchStatus.Purchasing, "Payment initiated");
        await _watchRepo.UpdateAsync(watch);

        _logger.LogInformation(
            "Watch {WatchId} transitioned to Purchasing. Executing payment: {Amount} {Currency} to {Seller}",
            watch.Id,
            match.Price,
            match.Currency,
            match.Seller);

        // Generate idempotency key
        var idempotencyKey = $"{matchId}:{approvalId}";

        // Execute payment via mock provider
        var paymentMethodToken = string.IsNullOrEmpty(watch.PaymentMethodToken)
            ? "tok_demo"
            : watch.PaymentMethodToken;

        var paymentResult = await _paymentProvider.ExecutePaymentAsync(
            idempotencyKey,
            match.Price,
            match.Currency,
            match.Seller,
            paymentMethodToken,
            ct);

        if (paymentResult.Success)
        {
            await HandlePaymentSuccessAsync(watch, match, approvalId, idempotencyKey, paymentResult, ct);
        }
        else
        {
            await HandlePaymentFailureAsync(watch, match, approvalId, idempotencyKey, paymentResult, ct);
        }
    }

    private async Task HandlePaymentSuccessAsync(
        WatchRequest watch,
        ProductMatch match,
        Guid approvalId,
        string idempotencyKey,
        PaymentResult paymentResult,
        CancellationToken ct)
    {
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            ApprovalId = approvalId,
            WatchRequestId = watch.Id,
            UserId = watch.UserId,
            IdempotencyKey = idempotencyKey,
            Amount = match.Price,
            Currency = match.Currency,
            Merchant = match.Seller,
            Status = PaymentStatus.Succeeded,
            PaymentProviderRef = paymentResult.ProviderReference ?? string.Empty,
            InitiatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = null
        };

        await _transactionRepo.CreateAsync(transaction);

        watch.UpdateStatus(WatchStatus.Completed, $"Payment succeeded, ref: {paymentResult.ProviderReference}");
        await _watchRepo.UpdateAsync(watch);

        var paymentCompletedEvent = new PaymentCompleted(
            MessageId: Guid.NewGuid(),
            CorrelationId: watch.Id,
            Timestamp: DateTimeOffset.UtcNow,
            Source: "payment-agent",
            TransactionId: transaction.Id,
            Amount: match.Price,
            Currency: match.Currency,
            Merchant: match.Seller);

        await _eventPublisher.PublishAsync(paymentCompletedEvent, TopicNames.PaymentCompleted, ct);

        _logger.LogInformation(
            "Payment succeeded for watch {WatchId}: {Amount} {Currency} to {Merchant}, ref: {ProviderRef}",
            watch.Id,
            match.Price,
            match.Currency,
            match.Seller,
            paymentResult.ProviderReference);
    }

    private async Task HandlePaymentFailureAsync(
        WatchRequest watch,
        ProductMatch match,
        Guid approvalId,
        string idempotencyKey,
        PaymentResult paymentResult,
        CancellationToken ct)
    {
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            ApprovalId = approvalId,
            WatchRequestId = watch.Id,
            UserId = watch.UserId,
            IdempotencyKey = idempotencyKey,
            Amount = match.Price,
            Currency = match.Currency,
            Merchant = match.Seller,
            Status = PaymentStatus.Failed,
            PaymentProviderRef = string.Empty,
            InitiatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            FailureReason = paymentResult.FailureReason
        };

        await _transactionRepo.CreateAsync(transaction);

        watch.UpdateStatus(WatchStatus.Active, $"Payment failed: {paymentResult.FailureReason}. Returning to active scanning.");
        await _watchRepo.UpdateAsync(watch);

        var paymentFailedEvent = new PaymentFailed(
            MessageId: Guid.NewGuid(),
            CorrelationId: watch.Id,
            Timestamp: DateTimeOffset.UtcNow,
            Source: "payment-agent",
            TransactionId: transaction.Id,
            Reason: paymentResult.FailureReason ?? "Unknown failure");

        await _eventPublisher.PublishAsync(paymentFailedEvent, TopicNames.PaymentFailed, ct);

        _logger.LogInformation(
            "Payment failed for watch {WatchId}: {Reason}. Watch returned to Active for re-scanning.",
            watch.Id,
            paymentResult.FailureReason);
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "PaymentWorker Service Bus error. Source: {ErrorSource}, Entity: {EntityPath}",
            args.ErrorSource,
            args.FullyQualifiedNamespace);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PaymentWorker stopping");

        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
```

---

## Section 3: Transaction Endpoints

### 3.1 Transaction Response Contract

**File:** `src/AgentPayWatch.Api/Contracts/TransactionResponse.cs`

```csharp
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Api.Contracts;

public sealed record TransactionResponse(
    Guid Id,
    Guid MatchId,
    Guid ApprovalId,
    Guid WatchRequestId,
    string UserId,
    string IdempotencyKey,
    decimal Amount,
    string Currency,
    string Merchant,
    string Status,
    string PaymentProviderRef,
    DateTimeOffset InitiatedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason)
{
    public static TransactionResponse FromEntity(PaymentTransaction entity) => new(
        Id: entity.Id,
        MatchId: entity.MatchId,
        ApprovalId: entity.ApprovalId,
        WatchRequestId: entity.WatchRequestId,
        UserId: entity.UserId,
        IdempotencyKey: entity.IdempotencyKey,
        Amount: entity.Amount,
        Currency: entity.Currency,
        Merchant: entity.Merchant,
        Status: entity.Status.ToString(),
        PaymentProviderRef: entity.PaymentProviderRef,
        InitiatedAt: entity.InitiatedAt,
        CompletedAt: entity.CompletedAt,
        FailureReason: entity.FailureReason);
}
```

### 3.2 Transaction Endpoints

**File:** `src/AgentPayWatch.Api/Endpoints/TransactionEndpoints.cs`

```csharp
using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AgentPayWatch.Api.Endpoints;

public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/transactions");

        group.MapGet("/", async (
            string userId,
            IPaymentTransactionRepository transactionRepo) =>
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.BadRequest(new { error = "userId query parameter is required." });
            }

            var transactions = await transactionRepo.GetByUserIdAsync(userId);

            var response = transactions
                .OrderByDescending(t => t.InitiatedAt)
                .Select(TransactionResponse.FromEntity)
                .ToList();

            return Results.Ok(response);
        })
        .WithName("GetTransactions")
        .WithOpenApi();

        group.MapGet("/{id:guid}", async (
            Guid id,
            string userId,
            IPaymentTransactionRepository transactionRepo) =>
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.BadRequest(new { error = "userId query parameter is required." });
            }

            var transaction = await transactionRepo.GetByIdAsync(id, userId);

            if (transaction is null)
            {
                return Results.NotFound(new { error = $"Transaction {id} not found." });
            }

            return Results.Ok(TransactionResponse.FromEntity(transaction));
        })
        .WithName("GetTransaction")
        .WithOpenApi();

        return routes;
    }
}
```

### 3.3 Update API Program.cs

**File:** `src/AgentPayWatch.Api/Program.cs`

Add the transaction endpoint mapping. The full updated file:

```csharp
using AgentPayWatch.Api.Endpoints;
using AgentPayWatch.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructureServices();

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapWatchEndpoints();
app.MapMatchEndpoints();
app.MapCallbackEndpoints();
app.MapTransactionEndpoints();

app.Run();
```

---

## Section 4: Program.cs for Payment Agent

**File:** `src/AgentPayWatch.Agents.Payment/Program.cs`

```csharp
using AgentPayWatch.Agents.Payment;
using AgentPayWatch.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructureServices();

builder.Services.AddHostedService<PaymentWorker>();

var host = builder.Build();

host.Run();
```

---

## Section 5: appsettings.json

**File:** `src/AgentPayWatch.Agents.Payment/appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information",
      "Azure.Messaging.ServiceBus": "Warning",
      "Azure.Core": "Warning"
    }
  },
  "Payment": {
    "MockSuccessRatePercent": 90
  }
}
```

---

## Section 6: Verification -- Full End-to-End Flow

This section walks through the complete lifecycle from watch creation to payment completion using only curl commands. Replace `{port}` with the actual API port shown in the Aspire dashboard.

### Step 1: Start the System

```bash
dotnet run --project appHost/apphost.cs
```

Open the Aspire dashboard (URL printed in console, typically `https://localhost:17225`). Verify all resources are running:
- `api` -- Running
- `product-watch-agent` -- Running
- `approval-agent` -- Running
- `payment-agent` -- Running
- `web` -- Running
- `cosmos` (emulator) -- Running
- `messaging` (Service Bus emulator) -- Running

### Step 2: Create a Watch

```bash
curl -X POST http://localhost:{port}/api/watches \
  -H "Content-Type: application/json" \
  -d '{
    "productName": "iPhone",
    "maxPrice": 999.00,
    "currency": "USD",
    "userId": "demo-user"
  }'
```

**Expected response:**
```json
{
  "id": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "userId": "demo-user",
  "productName": "iPhone",
  "maxPrice": 999.00,
  "currency": "USD",
  "status": "Active",
  "createdAt": "2026-02-20T...",
  "updatedAt": "2026-02-20T..."
}
```

Save the `id` value -- you will need it in subsequent steps. For these instructions, assume the returned id is `WATCH_ID`.

### Step 3: Wait for Product Watch Agent to Find a Match (~15-30 seconds)

The ProductWatchWorker polls every 15 seconds. After one or two cycles, check the watch status:

```bash
curl "http://localhost:{port}/api/watches?userId=demo-user"
```

**Expected:** The watch status should progress from `Active` to `Matched` to `AwaitingApproval`. The ApprovalWorker listens for the `ProductMatchFound` event and automatically transitions the watch. Wait until you see `AwaitingApproval`:

```json
{
  "id": "WATCH_ID",
  "status": "AwaitingApproval",
  ...
}
```

### Step 4: Get the Approval Token from Matches Endpoint

```bash
curl "http://localhost:{port}/api/matches/WATCH_ID"
```

**Expected response:**
```json
[
  {
    "id": "MATCH_ID",
    "watchRequestId": "WATCH_ID",
    "productName": "iPhone 15 Pro",
    "price": 849.99,
    "currency": "USD",
    "seller": "TechDeals Direct",
    "approvalToken": "APPROVAL_TOKEN_VALUE"
  }
]
```

Copy the `approvalToken` value.

### Step 5: Approve the Match via Callback

```bash
curl -X POST http://localhost:{port}/api/a2p/callback \
  -H "Content-Type: application/json" \
  -d '{
    "token": "APPROVAL_TOKEN_VALUE",
    "decision": "BUY"
  }'
```

**Expected response:** HTTP 200 with confirmation that the approval was recorded and the `ApprovalDecided` event was published.

### Step 6: Wait for Payment Agent (~2-3 seconds)

The PaymentWorker receives the `ApprovalDecided` event, transitions the watch to `Purchasing`, calls the mock payment provider (1-2 second simulated delay), and then transitions to `Completed` on success.

### Step 7: Verify Watch Completed

```bash
curl "http://localhost:{port}/api/watches/WATCH_ID?userId=demo-user"
```

**Expected response:**
```json
{
  "id": "WATCH_ID",
  "userId": "demo-user",
  "productName": "iPhone",
  "maxPrice": 999.00,
  "status": "Completed",
  "statusHistory": [
    { "from": "Active", "to": "Matched", "changedAt": "...", "reason": "..." },
    { "from": "Matched", "to": "AwaitingApproval", "changedAt": "...", "reason": "..." },
    { "from": "AwaitingApproval", "to": "Approved", "changedAt": "...", "reason": "..." },
    { "from": "Approved", "to": "Purchasing", "changedAt": "...", "reason": "Payment initiated" },
    { "from": "Purchasing", "to": "Completed", "changedAt": "...", "reason": "Payment succeeded, ref: PAY-..." }
  ]
}
```

### Step 8: Check Transactions Endpoint

```bash
curl "http://localhost:{port}/api/transactions?userId=demo-user"
```

**Expected response:**
```json
[
  {
    "id": "TRANSACTION_ID",
    "matchId": "MATCH_ID",
    "approvalId": "APPROVAL_ID",
    "watchRequestId": "WATCH_ID",
    "userId": "demo-user",
    "idempotencyKey": "MATCH_ID:APPROVAL_ID",
    "amount": 849.99,
    "currency": "USD",
    "merchant": "TechDeals Direct",
    "status": "Succeeded",
    "paymentProviderRef": "PAY-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
    "initiatedAt": "2026-02-20T...",
    "completedAt": "2026-02-20T...",
    "failureReason": null
  }
]
```

Verify a single transaction by ID:

```bash
curl "http://localhost:{port}/api/transactions/TRANSACTION_ID?userId=demo-user"
```

### Step 9: Check Aspire Traces

Open the Aspire dashboard and navigate to the **Traces** tab. You should see the complete event chain:

1. `api` -- POST /api/watches (watch created)
2. `product-watch-agent` -- ProductWatchWorker poll cycle, ProductMatch created, ProductMatchFound event published
3. `approval-agent` -- ApprovalWorker received ProductMatchFound, ApprovalRecord created, watch transitioned to AwaitingApproval
4. `api` -- POST /api/a2p/callback (approval submitted), ApprovalDecided event published
5. `payment-agent` -- PaymentWorker received ApprovalDecided, payment executed, PaymentTransaction created, PaymentCompleted event published

### Step 10: Test Payment Failure Scenario

The mock provider has a 10% failure rate. Create several watches to trigger failures:

```bash
for i in 1 2 3 4 5 6 7 8 9 10; do
  curl -s -X POST http://localhost:{port}/api/watches \
    -H "Content-Type: application/json" \
    -d "{
      \"productName\": \"iPhone\",
      \"maxPrice\": 999.00,
      \"currency\": \"USD\",
      \"userId\": \"demo-user\"
    }"
  echo ""
done
```

Wait for matches to appear (15-30 seconds each), then approve them all by fetching the approval tokens from the matches endpoint and submitting approvals.

After processing, check transactions:

```bash
curl "http://localhost:{port}/api/transactions?userId=demo-user"
```

**Expected:** Most transactions show `status: "Succeeded"`. Roughly 1 in 10 will show `status: "Failed"` with a `failureReason` such as "Insufficient funds", "Card declined", or "Provider timeout".

For failed payments, verify the associated watch returned to `Active`:

```bash
curl "http://localhost:{port}/api/watches?userId=demo-user"
```

Watches whose payments failed should show `status: "Active"` with a status history entry showing the transition from `Purchasing` back to `Active` with the failure reason. The ProductWatchWorker will automatically pick these watches up again on its next scan cycle and find new matches.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| PaymentWorker logs "Watch request not found in Approved status" | The watch may have already been processed by a previous message delivery. Check for duplicate processing. Ensure `AutoCompleteMessages = false` in the processor options. |
| Payment always succeeds (never fails) | Verify `appsettings.json` has `Payment:MockSuccessRatePercent` set to `90` (not `100`). |
| Payment always fails | Check that `Payment:MockSuccessRatePercent` is not set to `0`. Default is `90`. |
| Watch stuck in "Purchasing" status | Check payment-agent logs in Aspire dashboard. The mock provider may have thrown an exception. Look for unhandled errors in the ProcessMessageAsync handler. |
| "Insufficient funds" on every failure | This is expected behavior -- the failure reason is randomly selected from a small pool. Run more transactions to see variety. |
| Idempotency key collision | This is by design. If the same `{MatchId}:{ApprovalId}` combination is processed twice (e.g., due to Service Bus retry), the mock provider returns the cached result instead of processing again. |
| Service Bus emulator not starting | Ensure Docker is running. The Service Bus emulator runs as a container managed by Aspire. Check Docker logs if it fails. |
