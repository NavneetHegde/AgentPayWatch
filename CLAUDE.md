# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
# Build the solution
dotnet build AgentPayWatch.slnx

# Run everything (Aspire orchestration: API, agents, Blazor UI, Cosmos emulator)
aspire run  .\appHost\apphost.cs
```

There are no test projects yet (planned for a later phase).

## Architecture Overview

**AgentPayWatch** is a multi-agent, event-driven payment automation platform. Users create "watch requests" for products; autonomous agents monitor prices, request human approval via A2P messaging, and execute payments.

### Agent Workflow

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

### Project Layout

```
appHost/          - Aspire orchestration host (wires up all services + Cosmos emulator)
src/
  Domain/         - Entities, enums, domain events, repository interfaces (no dependencies)
  Infrastructure/ - Cosmos DB repositories, CosmosDbInitializer, DI registration
  Api/            - Minimal API endpoints (/api/watches, OpenAPI via Scalar)
  Web/            - Blazor Server UI
  ServiceDefaults/- Shared OpenTelemetry, resilience, service discovery config
  Agents.ProductWatch/  - BackgroundService that polls for matching products
  Agents.Approval/      - BackgroundService + ServiceBusProcessor for approval flow
  Agents.Payment/       - BackgroundService + ServiceBusProcessor for payment execution
docs/             - Phase-by-phase implementation plans and architecture decisions
```

### Key Technologies

- **.NET 10 / Aspire 13** — orchestration, local emulators, service discovery
- **Cosmos DB** — 4 containers: `watches` (pk: `/userId`), `matches` (pk: `/watchRequestId`), `approvals` (pk: `/watchRequestId`), `transactions` (pk: `/userId`). ETag-based optimistic concurrency.
- **Azure Service Bus** — Topics: `product-match-found`, `approval-decided`, `payment-completed`, `payment-failed`
- **Blazor Server** — avoids CORS complexity; uses service discovery to call API
- **Minimal APIs** — no MVC; endpoints defined in `*Endpoints.cs` files
- **OpenTelemetry** — traces/metrics/logs wired in `ServiceDefaults`

### WatchRequest State Machine

```
Active → Matched → AwaitingApproval → Approved → Purchasing → Completed
                                                             ↘ Active (retry)
Active → Paused → Active
Any non-terminal → Cancelled / Expired
```

State transitions are enforced in `WatchRequest.UpdateStatus()`. All changes recorded in `StatusHistory` (list of `StatusChange` value objects).

### Domain Events (C# records)

All events carry: `MessageId`, `CorrelationId`, `Timestamp`, `Source`, plus entity-specific fields. Published via `IEventPublisher`.

### Infrastructure Pattern

Repositories are registered as **Scoped** in `DependencyInjection.cs`. `CosmosDbInitializer` (IHostedService) creates the database and containers on startup with retry logic. Agents are **BackgroundService** implementations consuming Service Bus topics.

### Current Implementation Status

- **Done (Phase 1–2):** Domain models, Cosmos DB repositories, Watch CRUD API, Aspire orchestration
- **In progress / next:** Service Bus event backbone (Phase 3), agent implementations (Phase 4–6), Blazor UI (Phase 7)

Refer to `docs/agentpay-watch-execution-plan.md` and the individual `docs/phase-*.md` files for detailed implementation plans for each phase.
