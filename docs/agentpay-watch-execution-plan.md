# AgentPay Watch — Phased Execution Plan

> A build-and-test-incrementally approach. Each phase produces a running, verifiable system.

---

## Guiding Principle

Every phase ends with a **working build and a concrete test** you can run. No phase depends on "finishing everything first." You scaffold, verify, layer on, verify again.

---

## Phase 1: Foundation — Domain + Solution Scaffold

**Goal:** All projects exist, solution compiles, Aspire dashboard starts (even if services do nothing yet).

### 1.1 Create Domain Library

**Project:** `src/AgentPayWatch.Domain/AgentPayWatch.Domain.csproj`
- Pure class library, `net10.0`, no NuGet dependencies

**Files:**

| File | Contents |
|------|----------|
| `Enums/WatchStatus.cs` | `Active, Paused, Matched, AwaitingApproval, Approved, Purchasing, Completed, Expired, Cancelled` |
| `Enums/ApprovalDecision.cs` | `Pending, Approved, Rejected, Expired` |
| `Enums/PaymentStatus.cs` | `Initiated, Processing, Succeeded, Failed, Reversed` |
| `Enums/ProductAvailability.cs` | `InStock, LimitedStock, PreOrder` |
| `Enums/ApprovalMode.cs` | `AlwaysAsk, AutoApproveUnder` |
| `Enums/NotificationChannel.cs` | `A2P_RCS, A2P_SMS` |
| `Entities/WatchRequest.cs` | Aggregate root. Properties: Id, UserId, ProductName, MaxPrice, Currency, PreferredSellers, ApprovalMode, PaymentMethodToken, NotificationChannel, PhoneNumber, Status, CreatedAt, UpdatedAt, StatusHistory. Method: `UpdateStatus(newStatus)` enforcing valid state transitions. |
| `Entities/ProductMatch.cs` | Id, WatchRequestId, UserId, ProductName, Price, Currency, Seller, ProductUrl, MatchedAt, ExpiresAt, Availability |
| `Entities/ApprovalRecord.cs` | Id, MatchId, WatchRequestId, UserId, ApprovalToken, SentAt, ExpiresAt, RespondedAt?, Decision, Channel |
| `Entities/PaymentTransaction.cs` | Id, MatchId, ApprovalId, WatchRequestId, UserId, IdempotencyKey, Amount, Currency, Merchant, Status, PaymentProviderRef, InitiatedAt, CompletedAt?, FailureReason? |
| `ValueObjects/StatusChange.cs` | Record: `From, To, ChangedAt, Reason?` |
| `Events/ProductMatchFound.cs` | Record: MessageId, CorrelationId, Timestamp, Source, MatchId, ProductName, Price, Currency, Seller |
| `Events/ApprovalDecided.cs` | Record: MessageId, CorrelationId, Timestamp, Source, ApprovalId, MatchId, Decision |
| `Events/PaymentCompleted.cs` | Record: MessageId, CorrelationId, Timestamp, Source, TransactionId, Amount, Merchant |
| `Events/PaymentFailed.cs` | Record: MessageId, CorrelationId, Timestamp, Source, TransactionId, Reason |
| `Interfaces/IWatchRequestRepository.cs` | CreateAsync, GetByIdAsync, GetByUserIdAsync, UpdateAsync |
| `Interfaces/IProductMatchRepository.cs` | CreateAsync, GetByIdAsync, GetByWatchRequestIdAsync |
| `Interfaces/IApprovalRepository.cs` | CreateAsync, GetByIdAsync, GetByTokenAsync, UpdateAsync |
| `Interfaces/IPaymentTransactionRepository.cs` | CreateAsync, GetByIdAsync, GetByUserIdAsync, UpdateAsync |
| `Interfaces/IProductSource.cs` | `SearchAsync(productName, ct) → IReadOnlyList<ProductListing>` |
| `Interfaces/IEventPublisher.cs` | `PublishAsync<T>(message, topicName, ct)` |
| `Models/ProductListing.cs` | Record: Name, Price, Currency, Seller, Url, Availability |

### 1.2 Create Empty Project Shells

Create minimal `.csproj` + `Program.cs` for each project so the solution compiles:

| Project | SDK | References |
|---------|-----|------------|
| `src/AgentPayWatch.Infrastructure/` | `Microsoft.NET.Sdk` | Domain |
| `src/AgentPayWatch.Api/` | `Microsoft.NET.Sdk.Web` | Domain, Infrastructure, ServiceDefaults |
| `src/AgentPayWatch.Agents.ProductWatch/` | `Microsoft.NET.Sdk.Worker` | Domain, Infrastructure, ServiceDefaults |
| `src/AgentPayWatch.Agents.Approval/` | `Microsoft.NET.Sdk.Worker` | Domain, Infrastructure, ServiceDefaults |
| `src/AgentPayWatch.Agents.Payment/` | `Microsoft.NET.Sdk.Worker` | Domain, Infrastructure, ServiceDefaults |
| `src/AgentPayWatch.Web/` | `Microsoft.NET.Sdk.Web` | ServiceDefaults |

Each `Program.cs` is a minimal host that calls `AddServiceDefaults()` and runs.

### 1.3 Wire AppHost + Solution

**Modify `appHost/apphost.cs`:** Add `#:project` directives for all projects. Register each with `builder.AddProject<>()`.

**Modify `AgentPayWatch.slnx`:** Add all 7 new projects.

### Phase 1 Verification

```bash
# Must pass — all projects compile
dotnet build AgentPayWatch.slnx

# Must pass — Aspire dashboard opens, all services show as "Running"
# (they don't do anything yet, but they start and stay alive)
dotnet run --project appHost/apphost.cs
```

---

## Phase 2: Data Layer — Cosmos DB + Repositories

**Goal:** CRUD operations work against a real Cosmos DB emulator. Testable via API endpoints.

### 2.1 Infrastructure — Cosmos Repositories

**NuGet:** Add `Aspire.Microsoft.Azure.Cosmos` 10.1.0 to Infrastructure project.

| File | Purpose |
|------|---------|
| `Infrastructure/Cosmos/CosmosWatchRequestRepository.cs` | Partition key: `/userId`. CRUD operations. Uses `Container` from DI. |
| `Infrastructure/Cosmos/CosmosProductMatchRepository.cs` | Partition key: `/watchRequestId` |
| `Infrastructure/Cosmos/CosmosApprovalRepository.cs` | Partition key: `/matchId`. Includes cross-partition `GetByTokenAsync`. |
| `Infrastructure/Cosmos/CosmosPaymentTransactionRepository.cs` | Partition key: `/userId` |
| `Infrastructure/Cosmos/CosmosDbInitializer.cs` | `IHostedService` — creates database + 4 containers on startup. Safe to re-run. |
| `Infrastructure/DependencyInjection.cs` | `AddInfrastructureServices()` — registers repos, calls `builder.AddAzureCosmosClient("cosmos")` |

### 2.2 API — Watch CRUD Endpoints

| File | Endpoints |
|------|-----------|
| `Api/Endpoints/WatchEndpoints.cs` | `POST /api/watches`, `GET /api/watches`, `GET /api/watches/{id}`, `PUT /api/watches/{id}/pause`, `PUT /api/watches/{id}/resume`, `DELETE /api/watches/{id}` |
| `Api/Contracts/CreateWatchRequest.cs` | DTO: ProductName, MaxPrice, Currency?, PreferredSellers? |
| `Api/Contracts/WatchResponse.cs` | Maps from WatchRequest entity |
| `Api/Program.cs` | Add `AddInfrastructureServices()`, map endpoints |

### 2.3 AppHost — Add Cosmos Emulator

**Modify `appHost/apphost.cs`:**
```csharp
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator()
    .AddDatabase("agentpaywatch");
```
Add `.WithReference(cosmos).WaitFor(cosmos)` to API and all agents.

### Phase 2 Verification

```bash
# Start system (Cosmos emulator container starts automatically)
dotnet run --project appHost/apphost.cs

# Test CRUD via curl or any HTTP client:
# Create a watch
curl -X POST http://localhost:{port}/api/watches \
  -H "Content-Type: application/json" \
  -d '{"productName":"iPhone 15 Pro","maxPrice":999.99,"userId":"demo-user"}'

# List watches
curl http://localhost:{port}/api/watches?userId=demo-user

# Get specific watch
curl http://localhost:{port}/api/watches/{id}?userId=demo-user

# Pause watch
curl -X PUT http://localhost:{port}/api/watches/{id}/pause?userId=demo-user

# Verify: Cosmos Data Explorer (Aspire dashboard → cosmos resource) shows documents
```

---

## Phase 3: Event Backbone — Service Bus + Event Publishing

**Goal:** Events flow through Service Bus. Agents receive messages. Verifiable via Aspire traces.

### 3.1 Infrastructure — Service Bus Publisher

**NuGet:** Add `Aspire.Azure.Messaging.ServiceBus` 10.1.0 to Infrastructure project.

| File | Purpose |
|------|---------|
| `Infrastructure/Messaging/TopicNames.cs` | Constants: `product-match-found`, `approval-decided`, `payment-completed`, `payment-failed` |
| `Infrastructure/Messaging/ServiceBusEventPublisher.cs` | Implements `IEventPublisher`. Caches `ServiceBusSender` per topic. Sets `CorrelationId`, `Subject`, `MessageId` on each message. |

Update `DependencyInjection.cs` to register publisher and call `builder.AddAzureServiceBusClient("messaging")`.

### 3.2 AppHost — Add Service Bus Emulator

**Modify `appHost/apphost.cs`:**
```csharp
var serviceBus = builder.AddAzureServiceBus("messaging")
    .RunAsEmulator()
    .AddTopic("product-match-found", t => t.AddSubscription("approval-agent"))
    .AddTopic("approval-decided", t => t.AddSubscription("payment-agent"))
    .AddTopic("payment-completed")
    .AddTopic("payment-failed");
```
Add `.WithReference(serviceBus).WaitFor(serviceBus)` to API and all agents.

### 3.3 Test Endpoint — Manual Event Publish

Add a temporary test endpoint to the API:
```
POST /api/debug/publish-test-event
```
Publishes a dummy `ProductMatchFound` event to verify Service Bus connectivity.

### Phase 3 Verification

```bash
# Start system (both emulators now run)
dotnet run --project appHost/apphost.cs

# Publish a test event
curl -X POST http://localhost:{port}/api/debug/publish-test-event

# Verify in Aspire dashboard:
# 1. Service Bus emulator is running
# 2. Traces show the publish operation
# 3. No errors in any service logs
```

---

## Phase 4: Product Watch Agent — Autonomous Monitoring

**Goal:** Agent autonomously finds product matches and publishes events.

### 4.1 Mock Product Source

| File | Purpose |
|------|---------|
| `Infrastructure/Mocks/MockProductSource.cs` | Implements `IProductSource`. Hardcoded catalog (electronics, books, etc.). ~40% chance of returning a match below target price. Price jitter makes it realistic. |

Register in `DependencyInjection.cs`.

### 4.2 Agent Implementation

| File | Purpose |
|------|---------|
| `Agents.ProductWatch/ProductWatchWorker.cs` | `BackgroundService`. Every 15s: load Active watches → search each → match logic → create `ProductMatch` → update watch to `Matched` → publish `ProductMatchFound` |
| `Agents.ProductWatch/MatchingService.cs` | `EvaluateMatch(watch, listing)`: price ≤ maxPrice AND seller filter |
| `Agents.ProductWatch/appsettings.json` | `PollIntervalSeconds: 15` |

Update `Program.cs` to register `ProductWatchWorker` and `AddInfrastructureServices()`.

### Phase 4 Verification

```bash
# Start system
dotnet run --project appHost/apphost.cs

# Create a watch via API
curl -X POST http://localhost:{port}/api/watches \
  -d '{"productName":"iPhone 15 Pro","maxPrice":999.99,"userId":"demo-user"}'

# Wait 15-30 seconds, then check:
# 1. Watch status changed from Active to Matched
curl http://localhost:{port}/api/watches?userId=demo-user
# → status should be "Matched"

# 2. Aspire dashboard traces show:
#    - ProductWatchWorker polling
#    - ProductMatch created in Cosmos
#    - ProductMatchFound event published to Service Bus

# 3. Match endpoint returns the match
curl http://localhost:{port}/api/matches/{watchId}
```

---

## Phase 5: Approval Agent — Human-in-the-Loop

**Goal:** Match events trigger approval tokens. Users can approve/reject via API callback.

### 5.1 Mock A2P Client

| File | Purpose |
|------|---------|
| `Infrastructure/Mocks/MockA2PClient.cs` | `IA2PClient` interface + mock. `SendApprovalRequestAsync` logs the message content, returns success. |

### 5.2 Agent Implementation

| File | Purpose |
|------|---------|
| `Agents.Approval/ApprovalWorker.cs` | `ServiceBusProcessor` on `product-match-found/approval-agent`. Creates `ApprovalRecord` with crypto-random token (15-min TTL), updates watch to `AwaitingApproval`, calls mock A2P. |
| `Agents.Approval/ApprovalTimeoutWorker.cs` | Every 30s: scan expired Pending approvals → mark Expired → publish `ApprovalDecided(Expired)` → watch back to Active. |
| `Agents.Approval/appsettings.json` | `TokenExpiryMinutes: 15`, `TimeoutCheckIntervalSeconds: 30` |

### 5.3 API — Callback + Match Endpoints

| File | Purpose |
|------|---------|
| `Api/Endpoints/CallbackEndpoints.cs` | `POST /api/a2p/callback` — accepts `{token, decision}`. Validates token, checks not expired, updates approval decision, publishes `ApprovalDecided` event. |
| `Api/Endpoints/MatchEndpoints.cs` | `GET /api/matches/{watchId}` — returns matches with approval token for pending ones. |
| `Api/Contracts/ApprovalCallbackRequest.cs` | DTO: Token, Decision ("BUY" or "SKIP") |
| `Api/Contracts/MatchResponse.cs` | Includes approval token when status is AwaitingApproval |

### Phase 5 Verification

```bash
# Start system
dotnet run --project appHost/apphost.cs

# Create watch → wait for match (15-30s) → watch becomes AwaitingApproval
curl -X POST http://localhost:{port}/api/watches \
  -d '{"productName":"iPhone 15 Pro","maxPrice":999.99,"userId":"demo-user"}'

# Get the approval token from match details
curl http://localhost:{port}/api/matches/{watchId}
# → Note the approvalToken value

# Approve the match
curl -X POST http://localhost:{port}/api/a2p/callback \
  -d '{"token":"{approvalToken}","decision":"BUY"}'

# Verify:
# 1. Watch status → Approved
# 2. ApprovalDecided event published to Service Bus
# 3. Aspire traces show the full flow: match → approval token created → callback → event

# Test rejection:
# (create another watch, wait for match, then reject)
curl -X POST http://localhost:{port}/api/a2p/callback \
  -d '{"token":"{approvalToken}","decision":"SKIP"}'
# → Watch returns to Active, agent will find new matches

# Test timeout:
# (create watch, wait for match, do nothing for 15+ minutes)
# → ApprovalTimeoutWorker marks it Expired, watch returns to Active
```

---

## Phase 6: Payment Agent — Transaction Execution

**Goal:** Approved matches trigger mock payments. Full event flow completes.

### 6.1 Mock Payment Provider

| File | Purpose |
|------|---------|
| `Infrastructure/Mocks/MockPaymentProvider.cs` | `IPaymentProvider` interface + mock. 1-2s delay, 90% success rate, returns fake provider reference. Checks idempotency key for duplicates. |

### 6.2 Agent Implementation

| File | Purpose |
|------|---------|
| `Agents.Payment/PaymentWorker.cs` | `ServiceBusProcessor` on `approval-decided/payment-agent`. Filters `Decision == Approved`. Updates watch to `Purchasing` → generates idempotency key `{MatchId}:{ApprovalId}` → calls mock provider → on success: create transaction, watch → `Completed`, publish `PaymentCompleted` → on failure: watch → `Active`, publish `PaymentFailed`. |
| `Agents.Payment/appsettings.json` | `MockSuccessRatePercent: 90` |

### 6.3 API — Transaction Endpoints

| File | Purpose |
|------|---------|
| `Api/Endpoints/TransactionEndpoints.cs` | `GET /api/transactions?userId=`, `GET /api/transactions/{id}` |
| `Api/Contracts/TransactionResponse.cs` | DTO for transaction data |

### Phase 6 Verification — Full End-to-End (no UI)

```bash
# Start system
dotnet run --project appHost/apphost.cs

# === FULL FLOW ===

# 1. Create watch
curl -X POST http://localhost:{port}/api/watches \
  -d '{"productName":"iPhone 15 Pro","maxPrice":999.99,"userId":"demo-user"}'

# 2. Wait ~15-30s for Product Watch Agent to find a match

# 3. Check watch status (should be AwaitingApproval)
curl http://localhost:{port}/api/watches?userId=demo-user

# 4. Get approval token
curl http://localhost:{port}/api/matches/{watchId}

# 5. Approve
curl -X POST http://localhost:{port}/api/a2p/callback \
  -d '{"token":"{token}","decision":"BUY"}'

# 6. Wait ~2s for Payment Agent to execute

# 7. Verify completion
curl http://localhost:{port}/api/watches/{watchId}?userId=demo-user
# → status: "Completed"

curl http://localhost:{port}/api/transactions?userId=demo-user
# → shows successful transaction with amount, merchant, timestamp

# Aspire dashboard traces show the COMPLETE flow:
# CreateWatch → ProductWatchWorker scan → ProductMatchFound event
# → ApprovalWorker creates token → User callback → ApprovalDecided event
# → PaymentWorker executes → PaymentCompleted event
```

---

## Phase 7: Blazor Web UI — Interactive Dashboard

**Goal:** Full visual demo. No more curl commands needed.

### 7.1 Project Setup

**Blazor Server** (simpler than WASM for MVP — same components migrate later).

| File | Purpose |
|------|---------|
| `Web/Program.cs` | Blazor Server setup, `AddServiceDefaults()`, register `HttpClient` → `https+http://api` |
| `Web/Services/ApiClient.cs` | Typed HTTP client: `CreateWatchAsync`, `GetWatchesAsync`, `GetWatchAsync`, `GetMatchesAsync`, `GetTransactionsAsync`, `SubmitApprovalAsync` |

### 7.2 Layout

| File | Purpose |
|------|---------|
| `Web/Components/App.razor` | Root component |
| `Web/Components/Routes.razor` | Router |
| `Web/Components/Layout/MainLayout.razor` | Sidebar nav + content area |
| `Web/Components/Layout/NavMenu.razor` | Links: Dashboard, Create Watch, Watches, Transactions |

### 7.3 Pages

| Page | Features |
|------|----------|
| `Dashboard.razor` | Summary cards: Active Watches, Pending Approvals, Completed Purchases. 5s auto-refresh. |
| `CreateWatch.razor` | Form: Product Name, Max Price, Currency, Preferred Sellers. Submit → redirect to dashboard. |
| `WatchList.razor` | Table with color-coded status badges. Click → detail. 5s auto-refresh. |
| `WatchDetail.razor` | Watch info + status timeline. If AwaitingApproval: show match details + **Approve/Reject buttons**. Buttons call `/api/a2p/callback`. |
| `Transactions.razor` | Table: amount, merchant, status, date. |

**Status badge colors:** Active=blue, Matched=amber, AwaitingApproval=orange, Purchasing=purple, Completed=green, Failed/Expired=red.

### Phase 7 Verification — Visual Demo

```
1. dotnet run --project appHost/apphost.cs
2. Open Blazor UI (URL from Aspire dashboard)
3. Dashboard shows zero counts
4. Click "Create Watch" → fill form → submit
5. Dashboard shows 1 Active Watch
6. Wait 15-30s → status changes to AwaitingApproval (page auto-refreshes)
7. Click into watch detail → see match details → click "Approve"
8. Status transitions: Approved → Purchasing → Completed (within seconds)
9. Navigate to Transactions → see completed payment

Total demo time: ~60-90 seconds
```

---

## Phase Summary

| Phase | What You Build | What You Can Test | Dependencies |
|-------|---------------|-------------------|-------------|
| **1. Foundation** | Domain models + project shells + Aspire wiring | Solution compiles, Aspire dashboard starts | None |
| **2. Data Layer** | Cosmos repos + Watch CRUD API | Create/read/update watches via curl | Phase 1 |
| **3. Events** | Service Bus publisher + topic setup | Publish test events, verify in traces | Phase 2 |
| **4. Product Watch** | Mock product source + matching agent | Watch auto-matches within 15-30s | Phases 2, 3 |
| **5. Approval** | Mock A2P + approval agent + callback API | Approve/reject via curl, verify state transitions | Phase 4 |
| **6. Payment** | Mock payment + payment agent + transaction API | Full end-to-end flow via curl | Phase 5 |
| **7. UI** | Blazor dashboard + all pages | Full visual demo, no curl needed | Phase 6 |

Each phase is **independently testable**. If any phase breaks, you know exactly where the problem is.

---

## Technology Decisions

| Decision | Choice | Why |
|----------|--------|-----|
| Local data | Cosmos DB emulator via Aspire | Matches production behavior (partition keys, queries) |
| Local messaging | Service Bus emulator via Aspire | Real topic/subscription semantics |
| Agent pattern | `BackgroundService` + `ServiceBusProcessor` | Simple, proven, no framework overhead |
| UI framework | Blazor Server | No CORS, no WASM download, trivial service discovery |
| Auth | Hardcoded `demo-user` | MVP only; Entra ID in Phase 2 |
| Concurrency | Optimistic (ETag on WatchRequest) | Prevents agent race conditions on state updates |

---

## What Comes After Phase 7

These are **not part of this plan** but are the natural next steps (see `agentpay-watch-production-plan.md`):

- **Phase 8:** Google A2P RCS integration (replace mock A2P)
- **Phase 9:** Real payment provider (Stripe tokenized payments)
- **Phase 10:** Azure Foundry AI reasoning (match quality + risk scoring)
- **Phase 11:** Authentication (Microsoft Entra ID / Azure AD B2C)
- **Phase 12:** Production hardening (retry policies, DLQ handling, rate limiting, security audit, load testing)
