Review the current PR and post the review as a GitHub PR comment:

## Pre-Review Checklist
1. Find the current open PR for this branch using: `gh pr view --json number,title,url`
2. Show all commits included in this PR
3. Show full diff summary grouped by file
4. Confirm branch is up to date with main

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

## Build Review Report
Generate a structured review comment with these sections:

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

## Post to GitHub
After generating the review report:
1. Show me the full review text for confirmation
2. Ask for explicit approval before posting
3. Post the review as a PR comment using:
   `gh pr review --comment --body "<review text>"`
4. Confirm the comment was posted and show the PR URL

## Rules
- Never post to GitHub without explicit user confirmation
- Never approve or merge the PR — only post a review comment
- If no open PR exists for this branch, report it and stop
