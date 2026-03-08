Create a Pull Request for the current branch:

## Pre-PR Checklist
1. Confirm all changes are committed
2. Confirm branch is pushed to remote
3. Confirm we are NOT on main or master

## PR Content
Generate a PR with:
- Title: conventional commit style summary
- Description:
  ### What changed
  - Bullet points of key changes
  
  ### Why
  - Reason for the change
  
  ### How to test
  - Steps to verify the changes work
  
  ### Screenshots (if UI changes)
  - Placeholder reminder

## Create PR
- Use GitHub CLI: `gh pr create`
- Set base branch to main
- Ask me to review the title and description before submitting
- Add relevant labels if available (feat, fix, chore)
