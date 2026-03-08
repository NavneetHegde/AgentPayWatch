# Contributing Guide

This project uses Claude Code CLI for a consistent, 
safe development workflow. Follow this guide to contribute.

---

## Prerequisites
Before contributing, make sure you have:
- [ ] Claude Code CLI installed (`npm install -g @anthropic-ai/claude-code`)
- [ ] GitHub CLI installed (`winget install GitHub.cli` / `brew install gh`)
- [ ] GitHub CLI authenticated (`gh auth login`)
- [ ] Node.js installed
- [ ] Project dependencies installed (`npm install`)

---

## Workflow Overview
```
Write Code → Self Review → Create PR → Team Review → Merge
```

Every step has a Claude Code command to help you.

---

## Step by Step

### 1. Start a new feature
Always work on a feature branch, never on main:
```bash
git checkout -b feat/your-feature-name
```

### 2. Write your code
Work with Claude Code:
```bash
claude
```

### 3. Test your changes
Before committing, run unit tests:
```
/test-unit
```
Fix any failures before moving forward.

### 4. Commit your changes
Review and commit with conventional commit messages:
```
/commit-safe
```

Repeat steps 2-4 as needed during development.

### 5. Run full test suite
Before pushing, run all tests:
```
/test-integration
```

### 6. Self-review your code
Let Claude review your code before anyone else sees it:
```
/pr-review
```
Fix any issues flagged before proceeding.

### 7. Push your branch
```
/push
```

### 8. Create a Pull Request
```
/pr
```
This creates a PR with a descriptive title and description.

### 9. Team review
- Share the PR link with your team
- A reviewer can run `/pr-review-team` to post a structured review comment directly to the PR
- Address any review comments
- Re-run `/test-all` if changes were requested

### 10. Merge
Once approved on GitHub, merge via the GitHub UI.
Never merge directly from the command line.

---

## Full Workflow Diagram
```
┌─────────────────────────────────────────────────┐
│                 DEVELOPMENT LOOP                 │
│                                                  │
│  Write Code                                      │
│      ↓                                           │
│  /test-unit     ← fix failures before continuing │
│      ↓                                           │
│  /commit-safe   ← review diff before committing  │
│      ↓                                           │
│  repeat as needed                                │
└─────────────────────────────────────────────────┘
          ↓
┌─────────────────────────────────────────────────┐
│                  READY TO SHARE                  │
│                                                  │
│  /test-all      ← full test suite must pass      │
│      ↓                                           │
│  /pr-review     ← self review before team sees   │
│      ↓                                           │
│  /push          ← push to remote branch          │
│      ↓                                           │
│  /pr            ← create PR for team review      │
└─────────────────────────────────────────────────┘
          ↓
┌─────────────────────────────────────────────────┐
│                  TEAM REVIEW                     │
│                                                  │
│  Team reviews PR on GitHub                       │
│      ↓                                           │
│  /pr-review-team ← post structured review to PR │
│      ↓                                           │
│  Address feedback → /test-unit → /commit-safe    │
│      ↓                                           │
│  Approved ✅                                     │
│      ↓                                           │
│  Merge on GitHub UI                              │
└─────────────────────────────────────────────────┘
```

---

## Command Reference

### Testing
| Command | When to use |
|---------|-------------|
| `/test-unit` | Before every commit |
| `/test-integration` | Before every push |
| `/test-all` | Full check anytime |

### Committing
| Command | When to use |
|---------|-------------|
| `/commit-simple` | Small obvious changes |
| `/commit-safe` | Most commits — review first |
| `/commit-atomic` | Large changesets |
| `/commit-full` | Full end-to-end commit workflow |

### Pushing & PRs
| Command | When to use |
|---------|-------------|
| `/push` | After committing, ready to share |
| `/pr-review` | Self-review before creating PR |
| `/pr` | Create PR for team review |
| `/pr-review-team` | Post a structured review comment to the GitHub PR |

---

## Conventional Commits Reference

| Prefix | Use for | Example |
|--------|---------|---------|
| `feat:` | New feature | `feat: add user authentication` |
| `fix:` | Bug fix | `fix: resolve null pointer in login` |
| `refactor:` | Code restructure | `refactor: simplify auth middleware` |
| `docs:` | Documentation | `docs: update API reference` |
| `chore:` | Maintenance | `chore: update dependencies` |
| `test:` | Adding tests | `test: add unit tests for auth module` |

---

## Branch Naming

| Prefix | Use for | Example |
|--------|---------|---------|
| `feat/` | New features | `feat/user-auth` |
| `fix/` | Bug fixes | `fix/login-null-error` |
| `chore/` | Maintenance | `chore/update-deps` |
| `docs/` | Documentation | `docs/api-reference` |

---

## Golden Rules
- ❌ Never commit directly to main
- ❌ Never push without running tests
- ❌ Never merge your own PR without team review
- ❌ Never merge if tests are failing
- ✅ Always use feature branches
- ✅ Always self-review with /pr-review before creating PR
- ✅ Always use conventional commits
- ✅ Always let humans make the final merge decision


---
### Updated final file structure

```
AgentPayWatch/
├── README.md           ← project overview + quick start
├── CONTRIBUTING.md     ← full workflow guide ← new
├── CLAUDE.md           ← Claude rules and conventions
└── .claude/
    ├── README.md       ← command technical reference
    └── commands/
        ├── commit-simple.md
        ├── commit-safe.md
        ├── commit-atomic.md
        ├── commit-full.md
        ├── test-unit.md
        ├── test-integration.md
        ├── test-all.md
        ├── push.md
        ├── pr-review.md
        ├── pr-review-team.md
        └── pr.md
```

---

