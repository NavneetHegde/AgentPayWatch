# AgentPayWatch

A multi-agent, event-driven payment automation platform built with .NET 10 and Aspire 13. Users create "watch requests" for products; autonomous agents monitor prices, request human approval via A2P messaging, and execute payments.

## How It Works

```
User → Blazor UI → REST API → Cosmos DB
                                 ↓
         ProductWatch Agent (polls every 15s)
                 → publishes ProductMatchFound
                                 ↓
         Approval Agent → sends A2P message to user
                 → publishes ApprovalDecided (on user response)
                                 ↓
         Payment Agent → executes mock payment
                 → publishes PaymentCompleted/Failed
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [.NET latest Aspire](https://aspire.dev/get-started/install-cli/): `irm https://aspire.dev/install.ps1 | iex`
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for Cosmos DB and Service Bus emulators)

## Getting Started

```bash
# Build the solution
dotnet build AgentPayWatch.slnx

# Run everything (starts API, agents, Blazor UI, and local emulators)
aspire run .\appHost\apphost.cs
```

Aspire will start:
- **Cosmos DB emulator** — local database (no Azure account needed)
- **Service Bus emulator** — local message broker
- **API** — REST endpoints at `https://localhost:{port}/api`
- **ProductWatch Agent** — polls for matching products every 15s
- **Approval Agent** — handles A2P approval flow
- **Payment Agent** — executes payments on approval
- **Blazor UI** — interactive dashboard

Open the Aspire dashboard URL printed in the console to see all services and traces.

## Demo Flow

1. Open the Blazor UI (URL from Aspire dashboard)
2. Click **Create Watch** — enter a product name and max price
3. Wait 15–30 seconds — the ProductWatch Agent finds a matching product
4. Watch status changes to **Awaiting Approval**
5. Click into the watch detail — click **Approve**
6. Status transitions: Approved → Purchasing → Completed within seconds
7. Navigate to **Transactions** to see the completed payment

Total demo time: ~60–90 seconds

## Project Structure

```
appHost/                      - Aspire orchestration (wires up all services + emulators)
src/
  AgentPayWatch.Domain/       - Entities, enums, domain events, repository interfaces
  AgentPayWatch.Infrastructure/ - Cosmos DB repositories, Service Bus publisher, DI
  AgentPayWatch.Api/          - Minimal API endpoints (/api/watches, /api/matches, etc.)
  AgentPayWatch.Web/          - Blazor Server UI
  AgentPayWatch.ServiceDefaults/ - Shared OpenTelemetry, resilience, service discovery
  AgentPayWatch.Agents.ProductWatch/ - Polls for matching products every 15s
  AgentPayWatch.Agents.Approval/    - Handles approval token creation and A2P messaging
  AgentPayWatch.Agents.Payment/     - Executes payments on approved matches
tests/
  AgentPayWatch.Api.Tests/    - xUnit integration tests for all API endpoints
docs/                         - Phase-by-phase implementation plans
```

## API Endpoints

All watch endpoints require a `?userId=` query parameter.

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/watches` | Create a watch request |
| `GET` | `/api/watches` | List watches for a user |
| `GET` | `/api/watches/{id}` | Get a specific watch |
| `PUT` | `/api/watches/{id}/pause` | Pause an active watch |
| `PUT` | `/api/watches/{id}/resume` | Resume a paused watch |
| `DELETE` | `/api/watches/{id}` | Cancel a watch |
| `GET` | `/api/matches/{watchId}` | Get matches for a watch |
| `POST` | `/api/a2p/callback` | Submit approval decision (BUY/SKIP) |
| `GET` | `/api/transactions` | List transactions for a user |
| `GET` | `/api/transactions/{id}` | Get a specific transaction |

## WatchRequest State Machine

```
Active → Matched → AwaitingApproval → Approved → Purchasing → Completed
                                                             ↘ Active (on failure, retries)
Active → Paused → Active
Any non-terminal → Cancelled / Expired
```

## Key Technologies

| Technology | Purpose |
|------------|---------|
| .NET 10 / Aspire 13 | Orchestration, local emulators, service discovery |
| Cosmos DB | Persistent storage with optimistic concurrency (ETag) |
| Azure Service Bus | Event-driven agent coordination |
| Blazor Server | Interactive web UI (no CORS complexity) |
| Minimal APIs | Lightweight REST endpoints |
| OpenTelemetry | Distributed traces, metrics, and logs |

## Implementation Status

| Phase | Status | Description |
|-------|--------|-------------|
| 1. Foundation | Done | Domain models, project structure, Aspire wiring |
| 2. Data Layer | Done | Cosmos DB repositories, Watch CRUD API, integration tests |
| 3. Event Backbone | Planned | Service Bus publisher and topic setup |
| 4. ProductWatch Agent | Planned | Autonomous product monitoring |
| 5. Approval Agent | Planned | A2P messaging and approval flow |
| 6. Payment Agent | Planned | Payment execution and transaction recording |
| 7. Blazor UI | Planned | Interactive dashboard and approval interface |

See [`docs/agentpay-watch-execution-plan.md`](docs/agentpay-watch-execution-plan.md) for detailed phase plans.

## Running Tests

```bash
dotnet test tests/AgentPayWatch.Api.Tests/AgentPayWatch.Api.Tests.csproj
```

Tests use `WebApplicationFactory` with mocked repositories — no running emulators required.
