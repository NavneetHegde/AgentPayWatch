# AgentPay Watch — Production Architecture Plan

> **Agents watch. Humans approve. Payments happen. Safely.**

---

## 1. Design Principles

| Principle | Rationale |
|-----------|-----------|
| **Agent autonomy with human guardrails** | Agents act independently but never move money without explicit human approval |
| **Event-driven, not request-driven** | Loose coupling, replay-ability, natural audit trail |
| **Fail-safe over fail-fast** | A missed deal is acceptable; an unauthorized payment is not |
| **Observability as a first-class citizen** | Every agent decision must be traceable end-to-end |
| **Zero trust between agents** | Each agent validates its own inputs; no blind forwarding |
| **Idempotent everything** | Network retries, Service Bus redelivery, and duplicate callbacks must be safe |

---

## 2. Solution Structure

```
AgentPayWatch/
├── appHost/                          # Aspire orchestrator
├── src/
│   ├── AgentPayWatch.ServiceDefaults/    # Shared Aspire defaults (health, telemetry)
│   ├── AgentPayWatch.Api/                # .NET Minimal API (public surface)
│   ├── AgentPayWatch.Domain/             # Domain models, value objects, enums
│   ├── AgentPayWatch.Infrastructure/     # Cosmos DB, Service Bus, A2P client
│   ├── AgentPayWatch.Agents.ProductWatch/    # Product Watch Agent (worker)
│   ├── AgentPayWatch.Agents.Approval/        # Approval Agent (worker)
│   ├── AgentPayWatch.Agents.Payment/         # Payment Execution Agent (worker)
│   └── AgentPayWatch.Web/                    # Blazor WebAssembly frontend
├── tests/
│   ├── AgentPayWatch.Domain.Tests/
│   ├── AgentPayWatch.Api.Tests/
│   ├── AgentPayWatch.Agents.Tests/
│   └── AgentPayWatch.Integration.Tests/
└── docs/
```

---

## 3. Domain Model

### 3.1 Core Entities

```
WatchRequest (aggregate root)
├── Id: Guid
├── UserId: string
├── ProductName: string
├── MaxPrice: decimal
├── Currency: string (ISO 4217)
├── PreferredSellers: string[]
├── ApprovalMode: enum (AlwaysAsk | AutoApproveUnder)
├── AutoApproveThreshold: decimal?
├── PaymentMethodToken: string (tokenized, never raw card)
├── NotificationChannel: enum (A2P_RCS | A2P_SMS)
├── PhoneNumber: string (E.164)
├── Status: enum (Active | Paused | Matched | AwaitingApproval | Approved | Purchasing | Completed | Expired | Cancelled)
├── CreatedAt: DateTimeOffset
├── UpdatedAt: DateTimeOffset
└── StatusHistory: StatusChange[]

ProductMatch
├── Id: Guid
├── WatchRequestId: Guid
├── ProductName: string
├── Price: decimal
├── Currency: string
├── Seller: string
├── ProductUrl: string
├── MatchedAt: DateTimeOffset
├── ExpiresAt: DateTimeOffset
└── Availability: enum (InStock | LimitedStock | PreOrder)

ApprovalRecord
├── Id: Guid
├── MatchId: Guid
├── ApprovalToken: string (one-time, time-bound)
├── SentAt: DateTimeOffset
├── ExpiresAt: DateTimeOffset
├── RespondedAt: DateTimeOffset?
├── Decision: enum (Pending | Approved | Rejected | Expired)
└── Channel: enum (A2P_RCS | A2P_SMS)

PaymentTransaction
├── Id: Guid
├── MatchId: Guid
├── ApprovalId: Guid
├── IdempotencyKey: string
├── Amount: decimal
├── Currency: string
├── Merchant: string
├── Status: enum (Initiated | Processing | Succeeded | Failed | Reversed)
├── PaymentProviderRef: string
├── InitiatedAt: DateTimeOffset
├── CompletedAt: DateTimeOffset?
├── FailureReason: string?
└── RiskScore: decimal?
```

### 3.2 State Machine

```
            ┌──────────────┐
            │   Active     │ ← User creates watch
            └──────┬───────┘
                   │ Agent finds match
            ┌──────▼───────┐
            │   Matched    │
            └──────┬───────┘
                   │ A2P message sent
       ┌──────────-▼─────────┐
       │  AwaitingApproval   │
       └──┬───────┬──────┬───┘
          │       │      │
       Approve  Reject  Timeout
          │       │      │
    ┌─────▼──┐    │   ┌──▼──────┐
    │Approved│    │   │ Expired │ → re-activate watch
    └───┬────┘    │   └─────────┘
        │         │
  ┌─────▼─────┐   │
  │Purchasing │   │
  └──┬────┬───┘   │
     │    │       │
  Success Fail    │
     │    │       │
┌────▼─┐ ┌▼─────--▼──┐
│Done  │ │ Active    │ ← watch returns to monitoring
└──────┘ └───────────┘
```

---

## 4. Agent Design (Microsoft Agent Framework)

### 4.1 Product Watch Agent

**Runtime:** Background worker (hosted service), one per partition.

| Concern | Implementation |
|---------|---------------|
| **Scheduling** | Timer-based polling (configurable interval per watch, default 5 min) |
| **Product sources** | Pluggable `IProductSource` interface; start with mock, add real scrapers later |
| **Matching logic** | Price ≤ MaxPrice AND seller in PreferredSellers (or any if empty) |
| **AI reasoning** | Azure Foundry call to validate match quality (e.g., "Is this the same product?") |
| **Output** | Publishes `ProductMatchFound` message to Service Bus topic |
| **Duplicate guard** | Stores last match hash per watch; only emits if new match differs |
| **Failure** | Logs warning, increments failure counter, retries on next cycle |

```csharp
// Pseudo-contract
public interface IProductSource
{
    Task<IReadOnlyList<ProductListing>> SearchAsync(
        string productName, CancellationToken ct);
}
```

### 4.2 Approval Agent

**Runtime:** Service Bus topic subscriber (push-based).

| Concern | Implementation |
|---------|---------------|
| **Trigger** | `ProductMatchFound` message from Service Bus |
| **Approval token** | Cryptographically random, 15-min TTL, single-use |
| **A2P message** | Google RCS Business Messaging API (fallback: verified SMS) |
| **Message content** | Product name, price, seller, "Reply BUY or SKIP" — no sensitive data |
| **Callback** | `/a2p/callback` endpoint validates signature + token |
| **Timeout** | If no response within TTL, publish `ApprovalExpired` event |
| **Idempotency** | Token lookup prevents duplicate processing of the same callback |

### 4.3 Payment Execution Agent

**Runtime:** Service Bus topic subscriber (push-based).

| Concern | Implementation |
|---------|---------------|
| **Trigger** | `PaymentApproved` message from Service Bus |
| **Pre-validation** | Re-check price & availability (prices change fast) |
| **Risk check** | Azure Foundry evaluates transaction risk (amount, frequency, seller trust) |
| **Risk threshold** | Score > 0.8 → auto-block, notify user, log for review |
| **Payment execution** | Calls payment provider via tokenized payment method |
| **Idempotency key** | `{MatchId}:{ApprovalId}` — prevents double-charge on retry |
| **Confirmation** | Sends A2P confirmation message with order reference |
| **Failure handling** | Publishes `PaymentFailed` event; watch returns to Active |

---

## 5. Event Architecture

### 5.1 Service Bus Topology

```
Topics:
├── product-match-found
│   ├── Subscription: approval-agent
│   └── Subscription: audit-logger
├── approval-decided
│   ├── Subscription: payment-agent  (filter: Decision = Approved)
│   ├── Subscription: watch-reactivator (filter: Decision = Rejected | Expired)
│   └── Subscription: audit-logger
├── payment-completed
│   ├── Subscription: confirmation-sender
│   └── Subscription: audit-logger
└── payment-failed
    ├── Subscription: watch-reactivator
    └── Subscription: audit-logger
```

### 5.2 Message Contracts

All messages include a common envelope:

```json
{
  "messageId": "uuid",
  "correlationId": "uuid (= WatchRequestId)",
  "timestamp": "ISO8601",
  "source": "agent-name",
  "eventType": "ProductMatchFound",
  "payload": { ... }
}
```

### 5.3 Dead Letter & Retry Policy

| Setting | Value |
|---------|-------|
| Max delivery attempts | 5 |
| Retry delay | Exponential backoff (1s, 5s, 25s, 60s, 300s) |
| Dead letter queue | Enabled on all subscriptions |
| DLQ alert | Azure Monitor alert on DLQ depth > 0 |

---

## 6. API Surface

### 6.1 Public Endpoints (Blazor ↔ API)

```
POST   /api/watches              Create a new watch request
GET    /api/watches               List user's watches (paged)
GET    /api/watches/{id}          Get watch detail + status history
PUT    /api/watches/{id}/pause    Pause monitoring
PUT    /api/watches/{id}/resume   Resume monitoring
DELETE /api/watches/{id}          Cancel watch

GET    /api/matches/{watchId}     List matches for a watch
GET    /api/transactions          List user's transactions (paged)
GET    /api/transactions/{id}     Transaction detail

POST   /api/a2p/callback          Google A2P webhook (public, signature-verified)
```

### 6.2 Authentication & Authorization

| Concern | Implementation |
|---------|---------------|
| Identity provider | Microsoft Entra ID (Azure AD B2C for consumer) |
| Token format | JWT Bearer |
| API auth | `[Authorize]` on all `/api/*` except `/api/a2p/callback` |
| A2P callback auth | Google callback signature verification (HMAC) |
| Scopes | `watches.read`, `watches.write`, `transactions.read` |

---

## 7. Data Layer

### 7.1 Cosmos DB Design

| Container | Partition Key | Purpose |
|-----------|--------------|---------|
| `watches` | `/userId` | Watch requests + status history |
| `matches` | `/watchRequestId` | Product matches |
| `approvals` | `/matchId` | Approval records + tokens |
| `transactions` | `/userId` | Payment transactions |
| `audit` | `/correlationId` | Full event log for tracing |

### 7.2 Consistency & Throughput

- **Consistency level:** Session (sufficient for single-user reads-after-writes)
- **Indexing policy:** Exclude large string fields, include status + timestamp for queries
- **TTL:** Matches expire after 24h; Approval tokens expire after 15 min (application-enforced)
- **RU budgeting:** Start at 400 RU/s per container (autoscale to 4000)

---

## 8. Security

### 8.1 Payment Security

| Control | Detail |
|---------|--------|
| **No raw card data** | All payment methods stored as provider-issued tokens |
| **PCI scope** | Out of scope — tokenization offloads PCI to payment provider |
| **Approval tokens** | Cryptographically random, single-use, time-bound (15 min) |
| **Agent isolation** | Payment agent runs in its own container with minimal IAM permissions |
| **Idempotency** | Every payment call uses a deterministic idempotency key |
| **Amount verification** | Payment agent re-validates price before charging |

### 8.2 A2P Security

| Control | Detail |
|---------|--------|
| **Sender verification** | Google-verified business sender |
| **Callback validation** | HMAC signature on all inbound callbacks |
| **No sensitive data** | Messages contain product name + price only; no card/account data |
| **Rate limiting** | Max 1 A2P message per watch per 15 min window |
| **Opt-out** | `STOP` keyword immediately unsubscribes; stored in user preferences |

### 8.3 Infrastructure Security

- All secrets in **Azure Key Vault** (referenced via Aspire resource bindings)
- Managed identities for service-to-service auth (no connection strings in config)
- Network isolation via **Azure Virtual Network** integration on Container Apps
- **WAF** on the public API endpoint

---

## 9. Observability

### 9.1 Distributed Tracing

- **OpenTelemetry** via Aspire ServiceDefaults (already wired)
- Correlation ID = WatchRequestId flows through all messages and API calls
- Traces exported to **Azure Monitor / Application Insights**

### 9.2 Structured Logging

```
Every agent log entry includes:
  - CorrelationId (WatchRequestId)
  - AgentName
  - Action
  - Outcome (Success | Failure | Skipped)
  - DurationMs
  - Relevant entity IDs
```

### 9.3 Health Checks

| Check | Endpoint |
|-------|----------|
| API liveness | `/health/live` |
| API readiness | `/health/ready` (Cosmos + Service Bus connectivity) |
| Agent heartbeat | Each agent publishes heartbeat to Cosmos every 60s |

### 9.4 Alerts

| Alert | Condition |
|-------|-----------|
| Agent stopped | No heartbeat for 3 min |
| Payment failure spike | > 3 failures in 10 min |
| DLQ depth | Any dead letter queue > 0 messages |
| Approval timeout rate | > 50% of approvals timing out in 1h window |
| High risk score | Any transaction blocked by risk check |

---

## 10. Aspire Orchestration

```csharp
// appHost/apphost.cs — production wiring

var builder = DistributedApplication.CreateBuilder(args);

// Infrastructure
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .AddDatabase("agentpaywatch");

var serviceBus = builder.AddAzureServiceBus("messaging")
    .AddTopic("product-match-found", topic => {
        topic.AddSubscription("approval-agent");
        topic.AddSubscription("audit-logger");
    })
    .AddTopic("approval-decided", topic => {
        topic.AddSubscription("payment-agent");
        topic.AddSubscription("watch-reactivator");
        topic.AddSubscription("audit-logger");
    })
    .AddTopic("payment-completed", topic => {
        topic.AddSubscription("confirmation-sender");
        topic.AddSubscription("audit-logger");
    })
    .AddTopic("payment-failed", topic => {
        topic.AddSubscription("watch-reactivator");
        topic.AddSubscription("audit-logger");
    });

var keyVault = builder.AddAzureKeyVault("secrets");

// API
var api = builder.AddProject<Projects.AgentPayWatch_Api>("api")
    .WithReference(cosmos)
    .WithReference(serviceBus)
    .WithReference(keyVault)
    .WithExternalHttpEndpoints();

// Agents
builder.AddProject<Projects.AgentPayWatch_Agents_ProductWatch>("product-watch-agent")
    .WithReference(cosmos)
    .WithReference(serviceBus);

builder.AddProject<Projects.AgentPayWatch_Agents_Approval>("approval-agent")
    .WithReference(cosmos)
    .WithReference(serviceBus)
    .WithReference(keyVault);

builder.AddProject<Projects.AgentPayWatch_Agents_Payment>("payment-agent")
    .WithReference(cosmos)
    .WithReference(serviceBus)
    .WithReference(keyVault);

// Frontend
builder.AddProject<Projects.AgentPayWatch_Web>("web")
    .WithReference(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
```

---

## 11. Deployment (Azure Container Apps)

### 11.1 Environment Layout

```
Resource Group: rg-agentpaywatch-{env}
├── Azure Container Apps Environment
│   ├── api              (min 1, max 5 replicas, HTTP scale rule)
│   ├── product-watch     (1 replica, timer-driven)
│   ├── approval-agent    (min 1, max 3, Service Bus scale rule)
│   ├── payment-agent     (min 1, max 3, Service Bus scale rule)
│   └── web              (min 1, max 3, HTTP scale rule)
├── Azure Cosmos DB (serverless or provisioned based on load)
├── Azure Service Bus (Standard tier)
├── Azure Key Vault
├── Azure Monitor / App Insights
└── Azure Container Registry
```

### 11.2 CI/CD

| Stage | Tool |
|-------|------|
| Build & test | GitHub Actions |
| Container images | Docker → Azure Container Registry |
| Infrastructure | Bicep / azd (Azure Developer CLI) |
| Deployment | `azd up` for dev, GitHub Actions for staging/prod |
| Environments | dev → staging → production (manual gate on prod) |

---

## 12. Implementation Phases

### Phase 1 — Foundation (MVP)

| # | Task | Deliverable |
|---|------|-------------|
| 1 | Scaffold solution structure | All projects created, Aspire wiring compiles |
| 2 | Domain models + Cosmos DB repositories | CRUD for watches, matches, approvals, transactions |
| 3 | Minimal API endpoints | Create watch, list watches, get watch detail |
| 4 | Product Watch Agent (mock source) | Polls mock data, publishes match events |
| 5 | Service Bus integration | Topics, subscriptions, publish/subscribe working |
| 6 | Approval Agent (mock A2P) | Receives match, creates approval, auto-approves for demo |
| 7 | Payment Agent (mock payment) | Receives approval, simulates payment, records transaction |
| 8 | Blazor UI — dashboard + create watch | Basic functional UI |
| 9 | End-to-end demo flow | Create watch → match → approve → pay (all mocked) |

### Phase 2 — Real Integrations

| # | Task | Deliverable |
|---|------|-------------|
| 1 | Google A2P RCS integration | Real messages sent and callbacks received |
| 2 | Payment provider integration | Real tokenized payments (Stripe or similar) |
| 3 | Azure Foundry AI reasoning | Match quality validation + risk scoring |
| 4 | Authentication (Entra ID) | User login, JWT tokens, scoped access |
| 5 | Blazor UI — full screens | All views from UX spec |

### Phase 3 — Production Hardening

| # | Task | Deliverable |
|---|------|-------------|
| 1 | Retry policies + circuit breakers | Polly policies on all external calls |
| 2 | Dead letter queue handling | DLQ processor + alerts |
| 3 | Rate limiting | API rate limits + A2P message throttling |
| 4 | Security audit | Penetration test, secret rotation, WAF rules |
| 5 | Load testing | Verify scaling under 1000 concurrent watches |
| 6 | Monitoring dashboards | Grafana / Azure dashboards for ops team |

---

## 13. Key Production Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Double payment | Financial loss | Idempotency keys on every payment call |
| A2P message not delivered | Missed approval | Timeout → re-send once; then expire and notify via UI |
| Product price changes between match and payment | Overpay | Payment agent re-validates price; blocks if > MaxPrice |
| Agent crash mid-flow | Stuck state | Service Bus redelivery + status machine prevents skipping states |
| Cosmos DB throttling | Slow/failed operations | Autoscale RU + retry with backoff |
| Malicious callback spoofing | Unauthorized payment | HMAC signature verification on all A2P callbacks |

---

## 14. What Makes This Production-Ready (vs. Hackathon)

| Hackathon Version | Production Version |
|-------------------|--------------------|
| In-memory state | Cosmos DB with partitioning |
| Console.WriteLine | OpenTelemetry + structured logging |
| Happy path only | State machine with failure + timeout handling |
| No auth | Entra ID + JWT + scoped permissions |
| Mock payment | Tokenized payment with idempotency |
| Single instance | Autoscaling Container Apps with health checks |
| No retry | Exponential backoff + dead letter queues |
| Trust all input | HMAC-verified callbacks + input validation |
| No audit trail | Full event log in Cosmos with correlation IDs |

---

> **AgentPay Watch** — where autonomous agents handle the tedious work of finding deals, humans make the final call, and payments execute with the reliability and safety of a production financial system.
