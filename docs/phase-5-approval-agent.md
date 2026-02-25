# Phase 5: Approval Agent -- Human-in-the-Loop

> **Goal:** Match events trigger approval tokens. Users can approve or reject via an API callback. Watch status flows correctly through the state machine.

**Prerequisite:** Phase 4 complete (Product Watch Agent finding matches and publishing `ProductMatchFound` events to Service Bus).

---

## Section 1: Mock A2P Client

### Interface

**File:** `src/AgentPayWatch.Domain/Interfaces/IA2PClient.cs`

```csharp
namespace AgentPayWatch.Domain.Interfaces;

public interface IA2PClient
{
    /// <summary>
    /// Sends an approval request message to the user via A2P (Application-to-Person) messaging.
    /// Returns true if the message was accepted for delivery, false otherwise.
    /// </summary>
    Task<bool> SendApprovalRequestAsync(
        string phoneNumber,
        string productName,
        decimal price,
        string seller,
        string approvalToken,
        CancellationToken ct);
}
```

### Mock Implementation

**File:** `src/AgentPayWatch.Infrastructure/Mocks/MockA2PClient.cs`

```csharp
using AgentPayWatch.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Infrastructure.Mocks;

public sealed class MockA2PClient : IA2PClient
{
    private readonly ILogger<MockA2PClient> _logger;

    public MockA2PClient(ILogger<MockA2PClient> logger)
    {
        _logger = logger;
    }

    public Task<bool> SendApprovalRequestAsync(
        string phoneNumber,
        string productName,
        decimal price,
        string seller,
        string approvalToken,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "A2P Message to {PhoneNumber}: '{ProductName}' found at ${Price} from {Seller}. " +
            "Reply BUY to approve. Token: {Token}",
            phoneNumber,
            productName,
            price,
            seller,
            approvalToken);

        return Task.FromResult(true);
    }
}
```

### Register in DependencyInjection.cs

Update `src/AgentPayWatch.Infrastructure/DependencyInjection.cs` to include the A2P client registration. The full file after this change:

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
        // Cosmos DB
        builder.AddAzureCosmosClient("cosmos");
        builder.Services.AddHostedService<CosmosDbInitializer>();
        builder.Services.AddSingleton<IWatchRequestRepository, CosmosWatchRequestRepository>();
        builder.Services.AddSingleton<IProductMatchRepository, CosmosProductMatchRepository>();
        builder.Services.AddSingleton<IApprovalRepository, CosmosApprovalRepository>();
        builder.Services.AddSingleton<IPaymentTransactionRepository, CosmosPaymentTransactionRepository>();

        // Service Bus
        builder.AddAzureServiceBusClient("messaging");
        builder.Services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();

        // Mocks
        builder.Services.AddSingleton<IProductSource, MockProductSource>();
        builder.Services.AddSingleton<IA2PClient, MockA2PClient>();

        return builder;
    }
}
```

---

## Section 2: Approval Worker

**File:** `src/AgentPayWatch.Agents.Approval/ApprovalWorker.cs`

This `BackgroundService` listens to the `product-match-found` topic via the `approval-agent` subscription. When a match event arrives, it creates an approval record with a cryptographic token, sends a mock A2P message, and updates the watch status to `AwaitingApproval`.

```csharp
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

public sealed class ApprovalWorker : BackgroundService
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
            "approval-agent",
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
            "approval-agent");

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
            ProductMatch? productMatch = await _matchRepo.GetByIdAsync(matchEvent.MatchId, args.CancellationToken);
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

    public override async ValueTask DisposeAsync()
    {
        if (_processor is not null)
        {
            await _processor.DisposeAsync();
        }

        await base.DisposeAsync();
    }
}
```

---

## Section 3: Approval Timeout Worker

**File:** `src/AgentPayWatch.Agents.Approval/ApprovalTimeoutWorker.cs`

This `BackgroundService` runs on a 30-second timer. It scans for expired pending approvals, marks them as expired, reactivates the corresponding watch so the Product Watch Agent can re-scan, and publishes an `ApprovalDecided` event.

```csharp
using System.Text.Json;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentPayWatch.Agents.Approval;

public sealed class ApprovalTimeoutWorker : BackgroundService
{
    private readonly IApprovalRepository _approvalRepo;
    private readonly IWatchRequestRepository _watchRepo;
    private readonly IEventPublisher _eventPublisher;
    private readonly ILogger<ApprovalTimeoutWorker> _logger;
    private readonly TimeSpan _checkInterval;

    public ApprovalTimeoutWorker(
        IApprovalRepository approvalRepo,
        IWatchRequestRepository watchRepo,
        IEventPublisher eventPublisher,
        ILogger<ApprovalTimeoutWorker> logger,
        IConfiguration configuration)
    {
        _approvalRepo = approvalRepo;
        _watchRepo = watchRepo;
        _eventPublisher = eventPublisher;
        _logger = logger;

        int intervalSeconds = configuration.GetValue<int>(
            "Approval:TimeoutCheckIntervalSeconds", 30);
        _checkInterval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ApprovalTimeoutWorker started. Check interval: {IntervalSeconds}s",
            _checkInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckExpiredApprovalsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking expired approvals. Will retry next interval.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("ApprovalTimeoutWorker stopped.");
    }

    private async Task CheckExpiredApprovalsAsync(CancellationToken ct)
    {
        IReadOnlyList<ApprovalRecord> expiredApprovals =
            await _approvalRepo.GetPendingExpiredAsync(DateTimeOffset.UtcNow, ct);

        if (expiredApprovals.Count == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Found {Count} expired pending approval(s). Processing...",
            expiredApprovals.Count);

        foreach (var approval in expiredApprovals)
        {
            try
            {
                await ProcessExpiredApprovalAsync(approval, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error processing expired approval {ApprovalId} for watch {WatchRequestId}. Skipping.",
                    approval.Id,
                    approval.WatchRequestId);
            }
        }
    }

    private async Task ProcessExpiredApprovalAsync(ApprovalRecord approval, CancellationToken ct)
    {
        // Mark the approval as expired
        approval.Decision = ApprovalDecision.Expired;
        approval.RespondedAt = DateTimeOffset.UtcNow;
        await _approvalRepo.UpdateAsync(approval, ct);

        // Load the watch and reactivate it so the Product Watch Agent re-scans
        WatchRequest? watch = await _watchRepo.GetByIdAsync(
            approval.WatchRequestId,
            approval.UserId,
            ct);

        if (watch is not null)
        {
            watch.UpdateStatus(WatchStatus.Active);
            await _watchRepo.UpdateAsync(watch, ct);
        }

        // Publish ApprovalDecided event with Expired decision
        var decidedEvent = new ApprovalDecided(
            MessageId: Guid.NewGuid().ToString(),
            CorrelationId: approval.WatchRequestId.ToString(),
            Timestamp: DateTimeOffset.UtcNow,
            Source: "approval-agent",
            ApprovalId: approval.Id,
            MatchId: approval.MatchId,
            Decision: ApprovalDecision.Expired
        );

        await _eventPublisher.PublishAsync(decidedEvent, TopicNames.ApprovalDecided, ct);

        _logger.LogInformation(
            "Approval {ApprovalId} expired for watch {WatchRequestId}. Watch reactivated.",
            approval.Id,
            approval.WatchRequestId);
    }
}
```

---

## Section 4: Callback Endpoint

### Request Contract

**File:** `src/AgentPayWatch.Api/Contracts/ApprovalCallbackRequest.cs`

```csharp
namespace AgentPayWatch.Api.Contracts;

public sealed record ApprovalCallbackRequest(
    string Token,
    string Decision
);
```

### Callback Endpoints

**File:** `src/AgentPayWatch.Api/Endpoints/CallbackEndpoints.cs`

```csharp
using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Events;
using AgentPayWatch.Domain.Interfaces;
using AgentPayWatch.Infrastructure.Messaging;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentPayWatch.Api.Endpoints;

public static class CallbackEndpoints
{
    public static void MapCallbackEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/a2p");

        group.MapPost("/callback", HandleApprovalCallback)
            .WithName("ApprovalCallback")
            .WithOpenApi();
    }

    private static async Task<Results<Ok<ApprovalCallbackResponse>, NotFound<string>, BadRequest<string>>>
        HandleApprovalCallback(
            ApprovalCallbackRequest request,
            IApprovalRepository approvalRepo,
            IWatchRequestRepository watchRepo,
            IEventPublisher eventPublisher,
            CancellationToken ct)
    {
        // Validate input
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return TypedResults.BadRequest("Token is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Decision))
        {
            return TypedResults.BadRequest("Decision is required. Use 'BUY' or 'SKIP'.");
        }

        // Look up the approval record by token
        var approval = await approvalRepo.GetByTokenAsync(request.Token, ct);
        if (approval is null)
        {
            return TypedResults.NotFound("Approval token not found.");
        }

        // Validate the approval is still pending
        if (approval.Decision != ApprovalDecision.Pending)
        {
            return TypedResults.BadRequest(
                $"Approval has already been resolved with decision: {approval.Decision}.");
        }

        // Validate the token has not expired
        if (approval.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return TypedResults.BadRequest("Approval token has expired.");
        }

        // Map the decision string to the enum
        ApprovalDecision decision = request.Decision.ToUpperInvariant() switch
        {
            "BUY" => ApprovalDecision.Approved,
            "SKIP" => ApprovalDecision.Rejected,
            _ => ApprovalDecision.Pending // sentinel for invalid input
        };

        if (decision == ApprovalDecision.Pending)
        {
            return TypedResults.BadRequest(
                $"Invalid decision '{request.Decision}'. Use 'BUY' to approve or 'SKIP' to reject.");
        }

        // Update the approval record
        approval.Decision = decision;
        approval.RespondedAt = DateTimeOffset.UtcNow;
        await approvalRepo.UpdateAsync(approval, ct);

        // Load and update the watch request status
        var watch = await watchRepo.GetByIdAsync(approval.WatchRequestId, approval.UserId, ct);
        if (watch is not null)
        {
            if (decision == ApprovalDecision.Approved)
            {
                // Approved: move to Approved status (Payment Agent picks it up in Phase 6)
                watch.UpdateStatus(WatchStatus.Approved);
            }
            else
            {
                // Rejected: return to Active so the Product Watch Agent re-scans for new matches
                watch.UpdateStatus(WatchStatus.Active);
            }

            await watchRepo.UpdateAsync(watch, ct);
        }

        // Publish ApprovalDecided event
        var decidedEvent = new ApprovalDecided(
            MessageId: Guid.NewGuid().ToString(),
            CorrelationId: approval.WatchRequestId.ToString(),
            Timestamp: DateTimeOffset.UtcNow,
            Source: "api-callback",
            ApprovalId: approval.Id,
            MatchId: approval.MatchId,
            Decision: decision
        );

        await eventPublisher.PublishAsync(decidedEvent, TopicNames.ApprovalDecided, ct);

        var response = new ApprovalCallbackResponse(
            ApprovalId: approval.Id,
            Decision: decision.ToString(),
            WatchRequestId: approval.WatchRequestId,
            RespondedAt: approval.RespondedAt.Value
        );

        return TypedResults.Ok(response);
    }
}

public sealed record ApprovalCallbackResponse(
    Guid ApprovalId,
    string Decision,
    Guid WatchRequestId,
    DateTimeOffset RespondedAt
);
```

---

## Section 5: Match Endpoint with Approval Info

### Response Contract

**File:** `src/AgentPayWatch.Api/Contracts/MatchResponse.cs`

```csharp
using AgentPayWatch.Domain.Enums;

namespace AgentPayWatch.Api.Contracts;

public sealed record MatchResponse(
    Guid Id,
    Guid WatchRequestId,
    string UserId,
    string ProductName,
    decimal Price,
    string Currency,
    string Seller,
    string ProductUrl,
    DateTimeOffset MatchedAt,
    DateTimeOffset ExpiresAt,
    ProductAvailability Availability,
    string? ApprovalToken,
    string? ApprovalDecision,
    DateTimeOffset? ApprovalExpiresAt
);
```

### Match Endpoints

**File:** `src/AgentPayWatch.Api/Endpoints/MatchEndpoints.cs`

```csharp
using AgentPayWatch.Api.Contracts;
using AgentPayWatch.Domain.Entities;
using AgentPayWatch.Domain.Enums;
using AgentPayWatch.Domain.Interfaces;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentPayWatch.Api.Endpoints;

public static class MatchEndpoints
{
    public static void MapMatchEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/matches");

        group.MapGet("/{watchId:guid}", GetMatchesByWatchId)
            .WithName("GetMatchesByWatchId")
            .WithOpenApi();
    }

    private static async Task<Ok<List<MatchResponse>>> GetMatchesByWatchId(
        Guid watchId,
        IProductMatchRepository matchRepo,
        IApprovalRepository approvalRepo,
        CancellationToken ct)
    {
        IReadOnlyList<ProductMatch> matches = await matchRepo.GetByWatchRequestIdAsync(watchId, ct);

        var responses = new List<MatchResponse>();

        foreach (var match in matches)
        {
            // Try to find the approval record for this match
            string? approvalToken = null;
            string? approvalDecision = null;
            DateTimeOffset? approvalExpiresAt = null;

            ApprovalRecord? approval = await approvalRepo.GetByMatchIdAsync(match.Id, ct);
            if (approval is not null)
            {
                // Only expose the token if the approval is still pending and not expired
                if (approval.Decision == ApprovalDecision.Pending
                    && approval.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    approvalToken = approval.ApprovalToken;
                }

                approvalDecision = approval.Decision.ToString();
                approvalExpiresAt = approval.ExpiresAt;
            }

            responses.Add(new MatchResponse(
                Id: match.Id,
                WatchRequestId: match.WatchRequestId,
                UserId: match.UserId,
                ProductName: match.ProductName,
                Price: match.Price,
                Currency: match.Currency,
                Seller: match.Seller,
                ProductUrl: match.ProductUrl,
                MatchedAt: match.MatchedAt,
                ExpiresAt: match.ExpiresAt,
                Availability: match.Availability,
                ApprovalToken: approvalToken,
                ApprovalDecision: approvalDecision,
                ApprovalExpiresAt: approvalExpiresAt
            ));
        }

        return TypedResults.Ok(responses);
    }
}
```

**Note:** The `GetByMatchIdAsync` method must exist on `IApprovalRepository`. If it does not yet exist, add it:

```csharp
// In src/AgentPayWatch.Domain/Interfaces/IApprovalRepository.cs, add:
Task<ApprovalRecord?> GetByMatchIdAsync(Guid matchId, CancellationToken ct);
```

And implement it in `CosmosApprovalRepository`:

```csharp
// In src/AgentPayWatch.Infrastructure/Cosmos/CosmosApprovalRepository.cs, add:
public async Task<ApprovalRecord?> GetByMatchIdAsync(Guid matchId, CancellationToken ct)
{
    var query = new QueryDefinition(
        "SELECT * FROM c WHERE c.matchId = @matchId ORDER BY c.sentAt DESC OFFSET 0 LIMIT 1")
        .WithParameter("@matchId", matchId.ToString());

    using var iterator = _container.GetItemQueryIterator<ApprovalRecord>(
        query,
        requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(matchId.ToString()) });

    if (iterator.HasMoreResults)
    {
        var response = await iterator.ReadNextAsync(ct);
        return response.FirstOrDefault();
    }

    return null;
}
```

### Register Endpoints in API Program.cs

In `src/AgentPayWatch.Api/Program.cs`, add the endpoint mappings:

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

app.Run();
```

---

## Section 6: Program.cs for Approval Agent

**File:** `src/AgentPayWatch.Agents.Approval/Program.cs`

```csharp
using AgentPayWatch.Agents.Approval;
using AgentPayWatch.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddInfrastructureServices();

builder.Services.AddHostedService<ApprovalWorker>();
builder.Services.AddHostedService<ApprovalTimeoutWorker>();

var host = builder.Build();
await host.RunAsync();
```

---

## Section 7: Project File and appsettings.json

### Project File

**File:** `src/AgentPayWatch.Agents.Approval/AgentPayWatch.Agents.Approval.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\AgentPayWatch.Domain\AgentPayWatch.Domain.csproj" />
    <ProjectReference Include="..\AgentPayWatch.Infrastructure\AgentPayWatch.Infrastructure.csproj" />
    <ProjectReference Include="..\AgentPayWatch.ServiceDefaults\AgentPayWatch.ServiceDefaults.csproj" />
  </ItemGroup>

</Project>
```

### appsettings.json

**File:** `src/AgentPayWatch.Agents.Approval/appsettings.json`

```json
{
  "Approval": {
    "TokenExpiryMinutes": 15,
    "TimeoutCheckIntervalSeconds": 30
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "AgentPayWatch.Agents.Approval": "Debug"
    }
  }
}
```

---

## Section 8: Verification

### Step 1: Start the System

```bash
dotnet run --project appHost/apphost.cs
```

Wait for the Aspire dashboard to show all services as running: API, Product Watch Agent, Approval Agent, Cosmos emulator, and Service Bus emulator.

### Step 2: Create a Watch

```bash
curl -X POST http://localhost:5000/api/watches \
  -H "Content-Type: application/json" \
  -d '{
    "productName": "iPhone",
    "maxPrice": 999.00,
    "userId": "demo-user",
    "currency": "USD",
    "phoneNumber": "+15551234567",
    "paymentMethodToken": "tok_demo_visa",
    "notificationChannel": "A2P_SMS",
    "approvalMode": "AlwaysAsk"
  }'
```

Note the returned watch `id`.

### Step 3: Wait for Match (Phase 4 Agent Handles This)

Wait 15-30 seconds. The Product Watch Agent will find a match and publish a `ProductMatchFound` event.

### Step 4: Verify Watch Status is AwaitingApproval

```bash
curl http://localhost:5000/api/watches/{id}?userId=demo-user
```

The status should now be `AwaitingApproval`. The flow was:

1. Product Watch Agent found a match and set status to `Matched`
2. It published `ProductMatchFound` to Service Bus
3. Approval Agent consumed the event, created an approval token, and set status to `AwaitingApproval`

In the Approval Agent logs (Aspire dashboard), you should see:

```
info: AgentPayWatch.Agents.Approval.ApprovalWorker
      Approval requested for watch {WatchId}, token: {Token}, expires: {ExpiresAt}
info: AgentPayWatch.Infrastructure.Mocks.MockA2PClient
      A2P Message to +15551234567: 'iPhone 15 Pro' found at $912.43 from TechZone. Reply BUY to approve. Token: abc123...
```

### Step 5: Get the Approval Token from Match Endpoint

```bash
curl http://localhost:5000/api/matches/{watchId}
```

The response includes `approvalToken`, `approvalDecision` (should be `Pending`), and `approvalExpiresAt`. Copy the `approvalToken` value.

### Step 6: Test APPROVE Flow (BUY)

```bash
curl -X POST http://localhost:5000/api/a2p/callback \
  -H "Content-Type: application/json" \
  -d '{
    "token": "{paste-approval-token-here}",
    "decision": "BUY"
  }'
```

Expected response:

```json
{
  "approvalId": "...",
  "decision": "Approved",
  "watchRequestId": "...",
  "respondedAt": "2026-02-20T..."
}
```

Verify the watch status is now `Approved`:

```bash
curl http://localhost:5000/api/watches/{id}?userId=demo-user
```

The watch is now in `Approved` status. In Phase 6 (Payment Agent), this is where the Payment Agent picks it up. For now, it remains `Approved`.

### Step 7: Test REJECT Flow (SKIP)

Create a new watch, wait for it to reach `AwaitingApproval`, then reject:

```bash
# Create a new watch
curl -X POST http://localhost:5000/api/watches \
  -H "Content-Type: application/json" \
  -d '{
    "productName": "PlayStation",
    "maxPrice": 500.00,
    "userId": "demo-user",
    "currency": "USD",
    "phoneNumber": "+15551234567",
    "paymentMethodToken": "tok_demo_visa",
    "notificationChannel": "A2P_SMS",
    "approvalMode": "AlwaysAsk"
  }'

# Wait 15-30s for match + approval token creation
# Get the token
curl http://localhost:5000/api/matches/{newWatchId}

# Reject it
curl -X POST http://localhost:5000/api/a2p/callback \
  -H "Content-Type: application/json" \
  -d '{
    "token": "{paste-token}",
    "decision": "SKIP"
  }'

# Verify watch returned to Active
curl http://localhost:5000/api/watches/{newWatchId}?userId=demo-user
```

The watch status should be `Active` again. The Product Watch Agent will re-scan it on the next cycle and may find another match (with different price jitter).

### Step 8: Test Timeout Flow

Create another watch and wait for it to reach `AwaitingApproval`. Then do nothing for 15+ minutes (or temporarily lower `TokenExpiryMinutes` to 1 in appsettings for faster testing).

The `ApprovalTimeoutWorker` checks every 30 seconds. Once the token expires:

1. The approval record is marked as `Expired`
2. The watch status returns to `Active`
3. An `ApprovalDecided` event with `Decision = Expired` is published
4. The Product Watch Agent will re-scan the watch on the next cycle

Check the Approval Agent logs for:

```
info: AgentPayWatch.Agents.Approval.ApprovalTimeoutWorker
      Approval {ApprovalId} expired for watch {WatchRequestId}. Watch reactivated.
```

### Troubleshooting

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| Watch stuck at Matched, never goes to AwaitingApproval | Approval Agent not consuming from Service Bus | Check that the `approval-agent` subscription exists on the `product-match-found` topic in the AppHost. Verify the Approval Agent service is running in Aspire. |
| Callback returns 404 "token not found" | Token expired or wrong value | Copy the exact token from the match endpoint response. Ensure you are within the 15-minute window. |
| Callback returns 400 "already resolved" | Token was already used | Each token is single-use. Create a new watch to test again. |
| A2P log message not appearing | MockA2PClient not registered | Verify `DependencyInjection.cs` registers `IA2PClient` as `MockA2PClient`. |
| ApprovalTimeoutWorker not expiring tokens | Check interval or expiry time | Verify `appsettings.json` has the correct values. For faster testing, set `TokenExpiryMinutes` to 1. |

---

## Architecture Summary

After completing Phase 5, the full event-driven flow is:

```
User creates watch (API)
    |
    v
WatchRequest saved to Cosmos (status: Active)
    |
    v
ProductWatchWorker polls every 15s
    |
    v
Match found --> ProductMatch saved, status: Matched
    |
    v
ProductMatchFound event --> Service Bus topic
    |
    v
ApprovalWorker consumes event
    |
    v
ApprovalRecord created with crypto token (15-min TTL)
    |
    v
Mock A2P message logged, status: AwaitingApproval
    |
    v
User calls POST /api/a2p/callback with token
    |
    +---> BUY:  status --> Approved, ApprovalDecided(Approved) published
    |           (Phase 6 Payment Agent will pick this up)
    |
    +---> SKIP: status --> Active, ApprovalDecided(Rejected) published
    |           (Product Watch Agent re-scans)
    |
    +---> (no response within 15 min)
            ApprovalTimeoutWorker marks Expired
            status --> Active, ApprovalDecided(Expired) published
            (Product Watch Agent re-scans)
```

The `ApprovalDecided` event on the `approval-decided` topic is what Phase 6 (Payment Agent) will consume via its `payment-agent` subscription.
