# AgentPayWatch — Technical Walkthrough

This document walks through the full end-to-end flow with annotated code excerpts, explaining every architectural decision along the way.

---

## 1. Orchestration — One Command Starts Everything

`appHost/apphost.cs` is the single source of truth for the entire system. It uses .NET Aspire's declarative API to wire up infrastructure and services with zero YAML or docker-compose files.

```csharp
// Cosmos DB emulator — local NoSQL, Data Explorer UI included
var cosmosAccount = builder.AddAzureCosmosDB("cosmos")
    .RunAsPreviewEmulator(emulator => emulator.WithDataExplorer());
cosmosAccount.AddCosmosDatabase("agentpaywatch");

// Service Bus emulator — 4 topics declared here; subscriptions auto-created
var messaging = builder.AddAzureServiceBus("messaging").RunAsEmulator();

messaging.AddServiceBusTopic("product-match-found")
    .AddServiceBusSubscription("sub-approval-agent");

messaging.AddServiceBusTopic("approval-decided")
    .AddServiceBusSubscription("sub-payment-agent");

// Services declare their dependencies; Aspire handles WaitFor + connection strings
var api = builder.AddProject<Projects.AgentPayWatch_Api>("api")
    .WithReference(cosmosAccount).WaitFor(cosmosAccount)
    .WithReference(messaging).WaitFor(messaging)
    .WithExternalHttpEndpoints();

builder.AddProject<Projects.AgentPayWatch_Agents_ProductWatch>("product-watch-agent")
    .WithReference(cosmosAccount).WaitFor(cosmosAccount)
    .WithReference(messaging).WaitFor(messaging);
```

**Why this matters:** No environment variables to manage, no connection strings to copy. Aspire injects the right connection strings into each service at startup via service discovery. `WaitFor` ensures agents don't start before the emulators are ready.

---

## 2. Domain Model — State Machine as First-Class Code

`WatchRequest` is the aggregate root. Its state machine is encoded directly in a transition table — no switch statements scattered across agents.

```csharp
// src/AgentPayWatch.Domain/Entities/WatchRequest.cs

private static readonly Dictionary<WatchStatus, HashSet<WatchStatus>> AllowedTransitions = new()
{
    [WatchStatus.Active]           = [WatchStatus.Paused, WatchStatus.Matched,
                                      WatchStatus.Expired, WatchStatus.Cancelled],
    [WatchStatus.Matched]          = [WatchStatus.AwaitingApproval, WatchStatus.Active,
                                      WatchStatus.Cancelled],
    [WatchStatus.AwaitingApproval] = [WatchStatus.Approved, WatchStatus.Active,
                                      WatchStatus.Cancelled],
    [WatchStatus.Approved]         = [WatchStatus.Purchasing, WatchStatus.Cancelled],
    [WatchStatus.Purchasing]       = [WatchStatus.Completed, WatchStatus.Active],  // Active = retry
    [WatchStatus.Completed]        = [],   // terminal
    [WatchStatus.Cancelled]        = [],   // terminal
};

public void UpdateStatus(WatchStatus newStatus, string? reason = null)
{
    if (!AllowedTransitions[Status].Contains(newStatus))
        throw new InvalidOperationException($"Cannot transition from {Status} to {newStatus}.");

    StatusHistory.Add(new StatusChange(Status, newStatus, DateTimeOffset.UtcNow, reason));
    Status = newStatus;
    UpdatedAt = DateTimeOffset.UtcNow;
}
```

Every agent calls `watch.UpdateStatus(...)`. If an agent tries an illegal transition (e.g. `Completed → Active`) the domain throws — not the infrastructure, not a validation attribute. Invalid state is structurally impossible.

The `StatusHistory` list gives every watch a built-in audit trail, surfaced in the UI's detail page.

---

## 3. ProductWatch Agent — Autonomous Polling Loop

`ProductWatchWorker` is a `BackgroundService` that runs a scan every 15 seconds.

```csharp
// src/AgentPayWatch.Agents.ProductWatch/ProductWatchWorker.cs

protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        await ScanAsync(stoppingToken);           // process all active watches
        await Task.Delay(_pollInterval, stoppingToken);  // configurable, default 15s
    }
}

private async Task<bool> ProcessWatchAsync(WatchRequest watch, CancellationToken ct)
{
    // 1. Search the product catalog (mock: ~40 products, ±15% price jitter)
    var listings = await _productSource.SearchAsync(watch.ProductName, ct);

    // 2. Filter: price ≤ maxPrice, seller in preferredSellers (if specified)
    var bestMatch = listings
        .Where(l => MatchingService.IsMatch(watch, l))
        .OrderBy(l => l.Price)       // cheapest wins
        .FirstOrDefault();

    if (bestMatch is null) return false;

    // 3. Persist the match
    var productMatch = new ProductMatch { ... };
    await _matchRepo.CreateAsync(productMatch, ct);

    // 4. Advance the state machine
    watch.UpdateStatus(WatchStatus.Matched);
    await _watchRepo.UpdateAsync(watch, ct);   // ETag-based optimistic concurrency

    // 5. Fire event — Approval Agent wakes up
    await _eventPublisher.PublishAsync(
        new ProductMatchFound(MatchId: productMatch.Id, ...),
        TopicNames.ProductMatchFound, ct);

    return true;
}
```

**Key design choices:**
- Per-watch errors are caught and logged, but don't abort the scan cycle — one bad watch doesn't block the rest.
- `UpdateAsync` uses Cosmos ETags under the hood. If two agents somehow loaded the same watch, the second write will conflict and retry rather than silently overwriting.

---

## 4. Approval Agent — Event-Driven, Cryptographic Token

`ApprovalWorker` subscribes to `product-match-found` via Service Bus. It never polls Cosmos looking for new matches — it reacts to events.

```csharp
// src/AgentPayWatch.Agents.Approval/ApprovalWorker.cs

protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _processor = _serviceBusClient.CreateProcessor(
        TopicNames.ProductMatchFound,
        "sub-approval-agent",
        new ServiceBusProcessorOptions { AutoCompleteMessages = false });

    _processor.ProcessMessageAsync += ProcessMessageAsync;
    await _processor.StartProcessingAsync(stoppingToken);
    await Task.Delay(Timeout.Infinite, stoppingToken);  // park here; callbacks drive execution
}

private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
{
    var matchEvent = JsonSerializer.Deserialize<ProductMatchFound>(args.Message.Body.ToString());

    // 1. Load context from Cosmos (point reads — cheap and fast)
    var productMatch = await _matchRepo.GetByIdAsync(matchEvent.MatchId, matchEvent.CorrelationId);
    var watch        = await _watchRepo.GetByIdAsync(productMatch.WatchRequestId, productMatch.UserId);

    // 2. Generate a cryptographically secure, URL-safe approval token
    string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
        .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    // 3. Persist ApprovalRecord with 15-minute TTL
    await _approvalRepo.CreateAsync(new ApprovalRecord
    {
        ApprovalToken = token,
        ExpiresAt     = DateTimeOffset.UtcNow.AddMinutes(15),
        Decision      = ApprovalDecision.Pending,
        ...
    });

    // 4. Advance state machine
    watch.UpdateStatus(WatchStatus.AwaitingApproval);
    await _watchRepo.UpdateAsync(watch);

    // 5. Notify the user (mock: logs to console; real: SMS/RCS via A2P provider)
    await _a2pClient.SendApprovalRequestAsync(watch.PhoneNumber, productMatch.ProductName,
        productMatch.Price, productMatch.Seller, token);

    // 6. Complete — message leaves the queue
    await args.CompleteMessageAsync(args.Message);
}
```

On any exception, `AbandonMessageAsync` is called instead, returning the message to the Service Bus subscription for automatic redelivery. Tokens are 24 bytes of `RandomNumberGenerator` output — not `Random`, not a GUID.

---

## 5. Approval Callback — The Human Step

The user clicks Approve in the Blazor UI. The UI calls `POST /api/a2p/callback`. The API validates the token, updates state, and publishes the next event.

```csharp
// src/AgentPayWatch.Api/Endpoints/CallbackEndpoints.cs

private static async Task<Results<Ok<ApprovalCallbackResponse>, NotFound<string>, BadRequest<string>>>
    HandleApprovalCallback(ApprovalCallbackRequest request, ...)
{
    var approval = await approvalRepo.GetByTokenAsync(request.Token);

    // Guard: already decided?
    if (approval.Decision != ApprovalDecision.Pending)
        return TypedResults.BadRequest($"Already resolved: {approval.Decision}.");

    // Guard: expired?
    if (approval.ExpiresAt <= DateTimeOffset.UtcNow)
        return TypedResults.BadRequest("Approval token has expired.");

    // Map string → enum  ("BUY" → Approved, "SKIP" → Rejected)
    ApprovalDecision decision = request.Decision.ToUpperInvariant() switch
    {
        "BUY"  => ApprovalDecision.Approved,
        "SKIP" => ApprovalDecision.Rejected,
        _      => ApprovalDecision.Pending   // sentinel for invalid input → caught below
    };

    // Advance state machine
    watch.UpdateStatus(decision == ApprovalDecision.Approved
        ? WatchStatus.Approved
        : WatchStatus.Active);             // Rejected → back to scanning
    await watchRepo.UpdateAsync(watch);

    // Fire event → Payment Agent wakes up
    await eventPublisher.PublishAsync(
        new ApprovalDecided(Decision: decision, MatchId: approval.MatchId, ...),
        TopicNames.ApprovalDecided);

    return TypedResults.Ok(new ApprovalCallbackResponse(...));
}
```

A rejected watch goes back to `Active` — the ProductWatch Agent will find it again on the next scan cycle. The user gets another chance on the next match.

---

## 6. Payment Agent — Idempotent Execution

`PaymentWorker` subscribes to `approval-decided`. It filters immediately for `Approved` decisions and delegates to `PaymentProcessor` — a separate class that can be unit-tested without Service Bus.

```csharp
// src/AgentPayWatch.Agents.Payment/PaymentWorker.cs

if (approvalEvent.Decision != ApprovalDecision.Approved)
{
    // Rejected and Expired events — complete and move on
    await args.CompleteMessageAsync(args.Message);
    return;
}

await _processor.ProcessAsync(approvalEvent, args.CancellationToken);
await args.CompleteMessageAsync(args.Message);
```

```csharp
// src/AgentPayWatch.Agents.Payment/PaymentProcessor.cs

public async Task ProcessAsync(ApprovalDecided approvalEvent, CancellationToken ct)
{
    watch.UpdateStatus(WatchStatus.Purchasing, "Payment initiated");
    await _watchRepo.UpdateAsync(watch);

    // Idempotency key prevents double-charging on Service Bus redelivery
    var idempotencyKey = $"{matchId}:{approvalId}";

    var result = await _paymentProvider.ExecutePaymentAsync(
        idempotencyKey, match.Price, match.Currency, match.Seller, paymentToken, ct);

    if (result.Success)
    {
        // Record transaction, advance to Completed, publish PaymentCompleted
        await HandlePaymentSuccessAsync(...);
    }
    else
    {
        // Record failed transaction, return watch to Active (retry eligible)
        watch.UpdateStatus(WatchStatus.Active, $"Payment failed: {result.FailureReason}.");
        await HandlePaymentFailureAsync(...);
    }
}
```

**Idempotency key = `matchId:approvalId`** — deterministic and stable across retries. If Service Bus redelivers the message (e.g. after a crash), the payment provider sees the same key and returns the prior result instead of charging again.

On failure, the watch returns to `Active`. The ProductWatch Agent will scan it again, find a new match, and start the flow over — fully automatic retry.

---

## 7. Concurrency Safety — ETags in Cosmos

Every Cosmos write goes through optimistic concurrency. Here is how the repository retrieves and uses the ETag:

```csharp
// src/AgentPayWatch.Infrastructure/Cosmos/CosmosWatchRequestRepository.cs (simplified)

public async Task<WatchRequest?> GetByIdAsync(Guid id, string userId)
{
    var response = await _container.ReadItemAsync<WatchRequest>(
        id.ToString(), new PartitionKey(userId));

    var entity = response.Resource;
    entity.ETag = response.ETag;   // capture the server-side ETag
    return entity;
}

public async Task UpdateAsync(WatchRequest entity)
{
    var options = new ItemRequestOptions { IfMatchEtag = entity.ETag };
    await _container.ReplaceItemAsync(entity, entity.Id.ToString(),
        new PartitionKey(entity.UserId), options);
    // Throws CosmosException (412 Precondition Failed) if another writer changed it first
}
```

This means two agents cannot both update the same `WatchRequest` — the second write is rejected at the database level, not caught by application-level locks.

---

## 8. Event Schema — C# Records

All events are immutable C# records. Every event carries a common envelope (`MessageId`, `CorrelationId`, `Timestamp`, `Source`) plus event-specific fields.

```csharp
// src/AgentPayWatch.Domain/Events/

public record ProductMatchFound(
    Guid MessageId, Guid CorrelationId, DateTimeOffset Timestamp, string Source,
    Guid MatchId, string ProductName, decimal Price, string Currency, string Seller);

public record ApprovalDecided(
    Guid MessageId, Guid CorrelationId, DateTimeOffset Timestamp, string Source,
    Guid ApprovalId, Guid MatchId, ApprovalDecision Decision);

public record PaymentCompleted(
    Guid MessageId, Guid CorrelationId, DateTimeOffset Timestamp, string Source,
    Guid TransactionId, decimal Amount, string Currency, string Merchant);

public record PaymentFailed(
    Guid MessageId, Guid CorrelationId, DateTimeOffset Timestamp, string Source,
    Guid TransactionId, string Reason);
```

`CorrelationId` is always the `WatchRequestId`. This means any distributed trace or log query can join all events for a single watch using one ID.

---

## 9. Full Flow — Sequence Summary

```
User
 │
 │  POST /api/watches  { productName: "iPhone 15 Pro", maxPrice: 999 }
 ▼
API ──── creates WatchRequest (Status: Active) ──── Cosmos [watches]
 │
 │  201 Created
 ▼
Blazor UI (polls every 5s)

~15 seconds later...

ProductWatchWorker
 │
 │  SearchAsync("iPhone 15 Pro")  →  MockProductSource returns listings
 │  MatchingService.IsMatch(watch, listing) → price ≤ 999, seller OK
 │  CreateAsync(ProductMatch)  →  Cosmos [matches]
 │  watch.UpdateStatus(Matched)  →  Cosmos [watches]  (ETag check)
 │  PublishAsync(ProductMatchFound)  →  Service Bus [product-match-found]
 ▼

ApprovalWorker  (woken by Service Bus)
 │
 │  GetByIdAsync(MatchId)   →  Cosmos [matches]
 │  GetByIdAsync(WatchId)   →  Cosmos [watches]
 │  RandomNumberGenerator.GetBytes(24)  →  approvalToken
 │  CreateAsync(ApprovalRecord, ExpiresAt: +15min)  →  Cosmos [approvals]
 │  watch.UpdateStatus(AwaitingApproval)  →  Cosmos [watches]
 │  MockA2PClient.SendApprovalRequestAsync(...)  →  logs token to console
 │  CompleteMessageAsync()
 ▼

Blazor UI  (5s poll detects AwaitingApproval)
 │
 │  User opens WatchDetail, clicks "Approve"
 │  POST /api/a2p/callback  { token: "...", decision: "BUY" }
 ▼

CallbackEndpoints
 │
 │  GetByTokenAsync(token)  →  Cosmos [approvals]  (validates token, TTL)
 │  approval.Decision = Approved
 │  watch.UpdateStatus(Approved)  →  Cosmos [watches]
 │  PublishAsync(ApprovalDecided { Decision: Approved })  →  Service Bus [approval-decided]
 ▼

PaymentWorker  (woken by Service Bus)
 │
 │  Decision == Approved → proceed
 │  watch.UpdateStatus(Purchasing)  →  Cosmos [watches]
 │  idempotencyKey = "{matchId}:{approvalId}"
 │  MockPaymentProvider.ExecutePaymentAsync(...)  →  90% success, 1-2s delay
 │
 │  ── on success ──────────────────────────────────────────────
 │  CreateAsync(PaymentTransaction { Status: Succeeded })  →  Cosmos [transactions]
 │  watch.UpdateStatus(Completed)  →  Cosmos [watches]
 │  PublishAsync(PaymentCompleted)  →  Service Bus [payment-completed]
 │
 │  ── on failure ──────────────────────────────────────────────
 │  CreateAsync(PaymentTransaction { Status: Failed })  →  Cosmos [transactions]
 │  watch.UpdateStatus(Active)  →  watch re-enters scan queue
 │  PublishAsync(PaymentFailed)  →  Service Bus [payment-failed]
 ▼

Blazor UI  (5s poll)
 │  Transactions page shows Succeeded entry
 │  Dashboard card: "Completed Purchases" increments
```

---

## 10. Observability — Aspire Dashboard

Every service is instrumented with OpenTelemetry from `ServiceDefaults`:

```csharp
// src/AgentPayWatch.ServiceDefaults/Extensions.cs

builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("AgentPayWatch.*"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation());
```

The Aspire dashboard (URL printed at startup) shows:
- **Distributed traces** — follow a single watch request across all 5 services
- **Structured logs** — per-service, filterable by severity
- **Health endpoints** — `/health` and `/alive` on every service

---

## Key Numbers

| Metric | Value |
|--------|-------|
| Poll interval | 15 seconds (configurable) |
| Approval token TTL | 15 minutes |
| Product match TTL | 24 hours |
| Payment success rate (mock) | 90% |
| Payment latency (mock) | 1–2 seconds |
| Price jitter (mock) | ±15% |
| Cosmos containers | 4 (`watches`, `matches`, `approvals`, `transactions`) |
| Service Bus topics | 4 (`product-match-found`, `approval-decided`, `payment-completed`, `payment-failed`) |
| Services in Aspire | 7 (Cosmos, Service Bus, API, 3 agents, Blazor) |
