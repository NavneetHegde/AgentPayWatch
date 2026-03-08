# Claude Code Commands

> No command merges to `main` without human approval.

## Aspire

| Command | What it does |
|---|---|
| `/aspire-run` | Start Docker + Aspire host, auto-open dashboard |

## Testing

| Command | What it does |
|---|---|
| `/test-unit` | |
| `/test-integration` | |
| `/test-all` | Unit first, then integration |

## Committing (local only — nothing is pushed)

| Command | What it does |
|---|---|
| `/commit-simple` | |
| `/commit-safe` | Shows diff and explains changes before committing |
| `/commit-atomic` | Groups changes into separate logical commits |
| `/commit-full` | Atomic commits + integration tests + push |

## Push & PR

| Command | What it does |
|---|---|
| `/push` | Integration tests → confirm → push feature branch |
| `/pr` | Generates PR description → `gh pr create` |
| `/pr-review` | Code scan + tests → review report → human approves merge |
