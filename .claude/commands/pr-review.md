Review the current PR before merging:

## Pre-Review Checklist
1. Show all commits included in this PR
2. Show full diff summary grouped by file
3. Confirm branch is up to date with main

## Code Quality Checks
Scan for:
- Missing unit or integration tests
- Console.logs or debug code left in
- Hardcoded values or secrets
- TODO comments that should be resolved before merge
- Unused imports or dead code
- Functions without error handling

## Test Verification
1. Run unit tests — report results
2. Run integration tests — report results
3. Confirm all tests pass before proceeding

## PR Summary
Generate a review report:
### Commits
- List all commits with messages

### Files Changed
- List files changed with brief description of each change

### Test Coverage
- Tests passing: yes/no
- New tests added: yes/no
- Areas lacking coverage

### Potential Issues
- List any concerns found during review

### Recommendation
- ✅ Ready to merge / ❌ Needs work
- List blocking issues if any

## Final Step
Ask for confirmation before merging.
Never merge without explicit approval.
```

---

### Complete command quick reference
```
/test-unit          → run unit tests only
/test-integration   → run integration tests only
/test-all           → run full test suite

/commit-simple      → quick commit
/commit-safe        → review then commit
/commit-atomic      → clean multi-commit history
/commit-full        → full commit workflow

/push               → pre-check then push to remote
/pr                 → create pull request with description
/pr-review          → full review report before merging
/pr-review-team     → full review report + post comment to GitHub PR
