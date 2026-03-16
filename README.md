# AgentPayWatch

**Autonomous price-watching and payment execution powered by a multi-agent, event-driven architecture.**

AgentPayWatch lets users set a maximum price for any product. Three autonomous agents then work in concert — continuously monitoring prices, requesting human approval via A2P messaging, and executing the payment — all without the user lifting a finger beyond the initial approval click.

> Built with .NET 10, Aspire 13, Azure Cosmos DB, and Azure Service Bus — fully runnable locally with zero cloud dependencies.

---

## The Problem

Online prices change constantly — flash sales appear and vanish within hours, stock levels shift, and the best deal is often gone by the time a user happens to check. Current solutions (browser alerts, wishlist notifications) tell you a price dropped; they don't *act* on it. The user still has to notice the notification, open the site, re-authenticate, and complete the purchase — often after the deal has expired.

**Who this is for:** Anyone who regularly tracks products waiting for a price target — consumers hunting deals, procurement teams monitoring supplier quotes, or resellers watching inventory windows.

**Why it matters:** Closing the loop from "price matched" to "purchase completed" autonomously, with a single human approval step, turns a passive notification system into an active buying agent. The user sets intent once and walks away.

AgentPayWatch demonstrates this pattern end-to-end: autonomous monitoring, human-in-the-loop approval via A2P messaging, and automated payment execution — all coordinated by independent agents over an event bus, with no polling from the UI and no manual re-entry of payment details.

---

## Demo

> **Total end-to-end time: ~60–90 seconds**

1. Open the Blazor dashboard and click **Create Watch**
2. Enter a product name (e.g. "iPhone 15 Pro") and a max price
3. Wait 15–30 s — the ProductWatch Agent finds a matching product automatically
4. Watch status flips to **Awaiting Approval**
5. Open the watch detail and click **Approve**
6. Status cascades: `Approved → Purchasing → Completed` within seconds
7. Navigate to **Transactions** — the payment record appears

<!-- Add a screen recording or GIF here -->

---

## How It Works

```
┌─────────────┐     POST /api/watches      ┌─────────────┐     Cosmos DB
│  Blazor UI  │ ─────────────────────────▶ │   REST API  │ ──────────────▶ watches
│  (Blazor    │                            │  (Minimal   │                  matches
│   Server)   │ ◀── live status refresh ── │   APIs)     │                  approvals
└─────────────┘                            └─────────────┘                  transactions
                                                  │
                            ┌─────────────────────┼──────────────────────┐
                            ▼                     ▼                      ▼
                  ┌──────────────────┐  ┌──────────────────┐  ┌──────────────────┐
                  │  ProductWatch    │  │    Approval      │  │    Payment       │
                  │     Agent        │  │     Agent        │  │     Agent        │
                  │                  │  │                  │  │                  │
                  │ Polls every 15s  │  │ Consumes         │  │ Consumes         │
                  │ Finds matches    │  │ ProductMatchFound │  │ ApprovalDecided  │
                  │ Publishes event  │  │ Sends A2P msg    │  │ Executes payment │
                  └──────────────────┘  └──────────────────┘  └──────────────────┘
                            │                     │                      │
                            └──────── Azure Service Bus Topics ──────────┘
                                  product-match-found
                                  approval-decided
                                  payment-completed / payment-failed
```

### State Machine

Every WatchRequest moves through a strictly enforced state machine:

```
                     ┌─────────┐
                     │  Active │◀──────────────────────────────────────┐
                     └────┬────┘                                        │
                          │ ProductWatch Agent finds match              │ Payment fails
                          ▼                                             │ (auto-retry)
                     ┌─────────┐                                        │
                     │ Matched │                                        │
                     └────┬────┘                                        │
                          │ Approval Agent sends A2P message            │
                          ▼                                             │
               ┌──────────────────┐    Approval expires (15 min)       │
               │ AwaitingApproval │ ──────────────────────────────────▶│
               └────────┬─────────┘                                     │
                        │ User approves                                 │
                        ▼                                               │
                   ┌──────────┐                                         │
                   │ Approved │                                         │
                   └────┬─────┘                                         │
                        │ Payment Agent picks up                        │
                        ▼                                               │
                  ┌───────────┐                                         │
                  │ Purchasing│ ───────────────────────────────────────▶┘
                  └─────┬─────┘
                        │ Payment succeeds
                        ▼
                  ┌───────────┐
                  │ Completed │
                  └───────────┘

  Active → Paused → Active      (user-controlled)
  Any non-terminal → Cancelled  (user-controlled)
  Any non-terminal → Expired    (TTL / system)
```

---

## Architecture Highlights

| Concern | Approach |
|---------|----------|
| **Agent decoupling** | Agents communicate exclusively via Service Bus events — zero direct calls between agents |
| **Concurrency safety** | ETag-based optimistic concurrency on all Cosmos writes prevents agent race conditions |
| **Idempotency** | Payment Agent uses a stable idempotency key per (match, approval) pair — safe to retry |
| **Approval TTL** | Tokens expire after 15 minutes; a timeout worker resets the watch to Active automatically |
| **Local-first dev** | Cosmos DB and Service Bus emulators run via Docker — no Azure subscription required |
| **Observability** | OpenTelemetry traces, metrics, and structured logs across all 5 services via Aspire dashboard |
| **Clean architecture** | Domain project has zero NuGet dependencies; infrastructure, agents, and API depend inward |

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Aspire CLI](https://aspire.dev/get-started/install-cli/): `irm https://aspire.dev/install.ps1 | iex`
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (runs Cosmos DB and Service Bus emulators)

---

## Getting Started

```bash
# Clone and build
git clone https://github.com/NavneetHegde/AgentPayWatch
cd AgentPayWatch
dotnet build AgentPayWatch.slnx

# Run the full stack (API, 3 agents, Blazor UI, Cosmos, Service Bus)
aspire run .\appHost\apphost.cs
```

Aspire prints a dashboard URL in the console. Open it to see all services, their endpoints, and live telemetry.

### What starts up

| Service | Description |
|---------|-------------|
| Cosmos DB emulator | Local NoSQL store — 4 containers auto-created on startup |
| Service Bus emulator | Local message broker — 4 topics with subscriptions |
| API (`/api/*`) | REST endpoints + Scalar OpenAPI UI |
| ProductWatch Agent | Polls for matching products every 15 s |
| Approval Agent | Sends approval requests; handles timeouts |
| Payment Agent | Executes payments on approval |
| Blazor UI | Interactive dashboard with 5 s auto-refresh |

---

## API Reference

All watch and transaction endpoints take `?userId=` as a query parameter.

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/watches` | Create a watch request |
| `GET` | `/api/watches` | List watches for a user |
| `GET` | `/api/watches/{id}` | Get a watch with full status history |
| `PUT` | `/api/watches/{id}/pause` | Pause an active watch |
| `PUT` | `/api/watches/{id}/resume` | Resume a paused watch |
| `DELETE` | `/api/watches/{id}` | Cancel a watch |
| `GET` | `/api/matches/{watchId}` | Get product matches for a watch |
| `POST` | `/api/a2p/callback` | Submit approval decision (`BUY` / `SKIP`) |
| `GET` | `/api/transactions` | List payment transactions for a user |
| `GET` | `/api/transactions/{id}` | Get a specific transaction |

Interactive docs available at `/scalar` when the API is running.

---

## Project Structure

```
appHost/                            Aspire orchestration host
src/
  AgentPayWatch.Domain/             Entities, enums, events, interfaces — zero dependencies
  AgentPayWatch.Infrastructure/     Cosmos repos, Service Bus publisher, mock implementations
  AgentPayWatch.Api/                Minimal API endpoints and DTOs
  AgentPayWatch.Web/                Blazor Server dashboard
  AgentPayWatch.ServiceDefaults/    Shared OpenTelemetry, health checks, service discovery
  AgentPayWatch.Agents.ProductWatch/  Price monitoring agent (BackgroundService)
  AgentPayWatch.Agents.Approval/    Approval flow agent (ServiceBusProcessor + timeout worker)
  AgentPayWatch.Agents.Payment/     Payment execution agent (ServiceBusProcessor)
tests/
  AgentPayWatch.Api.Tests/          Integration tests (WebApplicationFactory, no emulators needed)
  AgentPayWatch.Infrastructure.Tests/
  AgentPayWatch.Agents.*.Tests/     Agent unit and integration tests
  AgentPayWatch.E2ETests/           Full end-to-end workflow tests
docs/                               Phase-by-phase implementation plans
```

---

## Running Tests

```bash
# Unit and API integration tests (no emulators required)
dotnet test tests/AgentPayWatch.Api.Tests/AgentPayWatch.Api.Tests.csproj
dotnet test tests/AgentPayWatch.Infrastructure.Tests/AgentPayWatch.Infrastructure.Tests.csproj
```

---

## Implementation Status

All 7 phases complete.

| Phase | Description |
|-------|-------------|
| 1. Foundation | Domain models, project scaffold, Aspire wiring |
| 2. Data Layer | Cosmos DB repositories, Watch CRUD API, integration tests |
| 3. Event Backbone | Service Bus publisher, 4 topics, event schema |
| 4. ProductWatch Agent | Autonomous 15 s polling, mock product catalog |
| 5. Approval Agent | A2P token flow, 15-min TTL, timeout worker |
| 6. Payment Agent | Mock payment execution, idempotency, transaction recording |
| 7. Blazor UI | Dashboard, watch list, detail, create, and transactions pages |

See [`docs/agentpay-watch-execution-plan.md`](docs/agentpay-watch-execution-plan.md) for the full plan.

For annotated code walkthroughs of every agent and design decision, see [`docs/walkthrough.md`](docs/walkthrough.md).

---

## Future Extensions — MCP and Agentic AI

The current architecture was deliberately designed around clean interfaces with mock implementations. Every seam that today holds a mock is an exact plug point for an AI upgrade — no structural changes required.

### Expose the API as an MCP Server

The REST API (`/api/watches`, `/api/matches`, `/api/a2p/callback`) already forms a complete, stateful tool surface. Wrapping it as an [MCP server](https://modelcontextprotocol.io) would let any MCP-compatible AI agent (Claude, Copilot, etc.) drive the entire watch lifecycle as native tool calls:

```
MCP Tool: create_watch      → POST /api/watches
MCP Tool: list_matches      → GET  /api/matches/{watchId}
MCP Tool: approve_purchase  → POST /api/a2p/callback { decision: "BUY" }
MCP Tool: list_transactions → GET  /api/transactions
```

A user could then interact entirely through natural language: *"Watch for a PS5 under $400 and buy it if it appears at GameStop"* — the AI agent manages the watch, polls for matches, and submits approval autonomously.

### Replace `IProductSource` with an AI-Powered Web Agent

Today `MockProductSource` returns a hardcoded 10-item catalog with price jitter. The `IProductSource` interface is the only contract the ProductWatch Agent depends on:

```csharp
public interface IProductSource
{
    Task<IReadOnlyList<ProductListing>> SearchAsync(string productName, CancellationToken ct);
}
```

Swapping in an AI implementation would give the agent real-world reach — searching live retailer pages, scraping structured data, and normalising results — without changing a single line of agent code. The agent calls `SearchAsync`; what happens inside is opaque to it.

### Replace `MatchingService` with LLM Reasoning

Today matching is two rules:

```csharp
// MatchingService.cs — the entire logic today
if (listing.Price > watch.MaxPrice) return false;
return PreferredSellers contains listing.Seller;
```

An LLM-based matcher could reason beyond price and seller:

- *Is this the right product variant?* ("128GB" vs "512GB" when the user asked for "iPhone 15 Pro")
- *Is this seller reputable?* (cross-reference reviews, return policies)
- *Is this price historically good?* (compare against a price-history tool)
- *Is stock genuinely limited or is "Limited Stock" a marketing label?*

The output is still a `bool` — the state machine and agent flow are unchanged.

### AI-Generated Approval Messages via `IA2PClient`

Today `MockA2PClient` logs a fixed string. A real implementation backed by an LLM could generate context-aware approval messages:

> *"Found iPhone 15 Pro (128GB, Space Black) at TechZone for $934 — 7% below your $999 target and $42 cheaper than yesterday's lowest price. Tap BUY to purchase or SKIP to keep watching."*

The `IA2PClient` interface accepts product name, price, seller, and token — all the context an LLM needs to compose a genuinely useful message.

### Replace Fixed Agents with an Agentic Orchestrator

The three `BackgroundService` agents are each a hardcoded loop: poll every 15s, send approval, process payment. An agentic AI layer could make these decisions dynamically:

- **Adaptive polling** — scan more frequently for high-priority watches or when price history suggests a drop is imminent
- **Multi-step reasoning** — before approving a match, the agent checks seller reviews, confirms the product URL resolves, and validates the listed availability
- **Autonomous escalation** — if a payment fails repeatedly, the agent recommends alternative sellers rather than silently retrying

The Service Bus topics remain the coordination backbone. The agents become reasoners rather than rule-executors, but the event contracts stay identical.

### Architectural fit

```
                        Today                    With MCP + Agentic AI
                        ─────                    ─────────────────────
IProductSource    →     MockProductSource    →   McpWebSearchProductSource
MatchingService   →     price ≤ max          →   LlmMatchingService (reasoning)
IA2PClient        →     MockA2PClient        →   LlmA2PClient (generated messages)
REST API          →     internal only        →   + MCP Server wrapper
Agents            →     fixed polling loops  →   + adaptive AI orchestration layer
```

No domain model changes. No state machine changes. No Cosmos schema changes. The event backbone stays intact — AI components slot in at the interfaces that already exist.

---

## Technology Stack

| Technology | Role |
|------------|------|
| .NET 10 / Aspire 13 | Orchestration, emulator management, service discovery |
| Azure Cosmos DB | Partitioned NoSQL storage with ETag concurrency |
| Azure Service Bus | Async event bus for agent coordination |
| Blazor Server | Server-side UI with no CORS complexity |
| Minimal APIs + Scalar | Lightweight REST layer with built-in OpenAPI docs |
| OpenTelemetry | Distributed traces, metrics, and structured logs |
| xUnit + WebApplicationFactory | Integration tests without live infrastructure |
