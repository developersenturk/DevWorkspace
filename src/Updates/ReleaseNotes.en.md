## Amend the last commit and see push status

- Amend is now available in the Git view. Edit the prefilled last commit message and include staged file changes in the same commit. Unstaged changes are preserved.
- Amend does not push automatically. It stops if HEAD or the staged changes have changed, and is unavailable before the first commit, on detached HEAD, or during ongoing Git operations.
- A Push status column in the main project list shows the number of commits waiting to be pushed. Branches without upstream tracking are identified separately. Counts use the last known remote state.
