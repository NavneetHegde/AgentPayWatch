Push current branch to remote:

## Pre-push Checklist
1. Confirm we are NOT on main or master — stop if we are
2. Run integration tests — stop if any fail
3. Check for any uncommitted changes — commit or stash first
4. Show which branch is being pushed
5. Ask for confirmation before pushing

## Push
- Push current branch to origin
- If remote branch doesn't exist, set upstream automatically
- Report push success with remote URL

## On Failure
- Show exact error
- Suggest fix (e.g. pull first if behind, force push warning)
- Never force push without explicit confirmation
