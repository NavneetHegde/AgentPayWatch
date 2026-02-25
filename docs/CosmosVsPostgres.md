> Why we choose Cosmos db and not postgres?

● Good question. Here's the honest trade-off analysis for this specific project:
  Why Cosmos DB fits AgentPay Watch better
  1. Data model is document-shaped, not relational

  The WatchRequest entity embeds StatusHistory (a list of state transitions) directly on the document.
   In Postgres you'd need a separate watch_status_history table with joins. Cosmos stores it naturally
   as a nested array.

  2. Event-driven agents need partition-local reads

  Each agent reads by a known partition key (userId, watchRequestId). Cosmos guarantees single-digit
  millisecond reads on partition key lookups — no query planner, no index choices, just a direct key
  lookup. Agents run on tight polling cycles (15s), so latency predictability matters.

  3. Aspire + Azure-native story

  The demo targets Azure Container Apps. Cosmos DB is Azure-native — Aspire provisions the emulator
  locally and the same code hits real Cosmos in production with zero config changes. Postgres on Azure
   (Flexible Server) works but requires more plumbing.

  4. No complex relational queries

  This system never joins tables. Queries are always:
  - "Give me all active watches" (status filter)
  - "Give me matches for this watchId" (partition key)
  - "Give me approvals for this token" (token lookup)

  Postgres shines when you need joins, aggregations, foreign keys. This system has none of those.

  ---
  When you should choose Postgres instead

  Situation: Complex reporting (sales totals, user analytics)
  Choose Postgres: Joins + aggregations are natural in SQL
  ────────────────────────────────────────
  Situation: Strict ACID transactions across multiple entities
  Choose Postgres: Postgres has full multi-row transactions
  ────────────────────────────────────────
  Situation: Team knows SQL well, not Cosmos
  Choose Postgres: Operational risk matters
  ────────────────────────────────────────
  Situation: Cost sensitivity at low scale
  Choose Postgres: Postgres on a small VM is cheap; Cosmos RU pricing adds up
  ────────────────────────────────────────
  Situation: Relational integrity (foreign keys, constraints)
  Choose Postgres: Postgres enforces these; Cosmos doesn't

  ---
  The honest answer

  For a hackathon/MVP with an Azure-first deployment target and document-shaped data, Cosmos DB is the
   right call.

  If this were a multi-tenant SaaS product with complex billing queries, user analytics, and a team of
   SQL-fluent engineers — Postgres would win.

  The good news: because the repositories sit behind interfaces (IWatchRequestRepository etc.),
  swapping the storage backend later is a contained change. Only the Infrastructure layer would
  change; Domain, Api, and all agents would be untouched.
