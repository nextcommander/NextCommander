---
name: Push all local branches to remote
description: Never leave a branch local-only after creating it or committing; pushing to origin with -u is mandatory (regardless of whether it's before or after merging to master)
type: feedback
---

After creating a local branch and committing, **always** run `git push -u origin <branch>`. Don't keep a branch only locally; even if you've already merged it into master, the feature branch must also exist on the remote.

**Why:** All branches need to be on the remote for backup and audit-trail purposes; also, if we move to a PR or review workflow, the branches should be ready.

**How to apply:**
- After opening a new branch, set up tracking on the first push with the `-u` flag: `git push -u origin <branch>`
- After committing, push either before merging to master (ready for review) or after merging (permanent record for audit); push in both cases
- Don't delete the feature branch after pushing master (until the user asks); keep it on origin
- You can bulk-push old local branches with a bash loop: `for br in $(git branch --list --format='%(refname:short)' | grep -v master); do git push -u origin "$br"; done`
- Don't use `--force-with-lease` or `--force`; a fast-forward push after a `--no-ff` merge is sufficient