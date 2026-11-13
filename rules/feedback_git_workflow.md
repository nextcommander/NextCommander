---
name: Git Workflow and Commit Rules
description: Before every commit, branch off development, commit, and merge back into development. The Conventional Commits standard is mandatory. For production releases, merge development into master and release from master.
type: feedback
---
Every commit is done in the following order:

1. `git checkout development && git pull` — update development
2. `git checkout -b <type>/<scope>-<short-description>` — create a new branch
3. Stage the changes and commit
4. `git checkout development && git merge <branch>` — merge into development
5. The branch can be deleted

**Why:** Branch-based work is mandatory to keep the history readable and make bug tracking easier.

**How to apply:** Applies before every commit. This holds for everything, including one-line fixes.

---

## Conventional Commits Format

```
<type>(<scope>): <short description>

[optional body: why the change was made]

[optional footer: closed issue numbers]
```

### Type Labels

| Label | Usage |
|--------|---------|
| `feature` | New feature |
| `fix` | Bug fix |
| `docs` | Documentation change |
| `style` | Visual changes that don't affect how the code runs |
| `refactor` | Neither a bug fix nor a new feature — code restructuring |
| `test` | Adding or fixing tests |
| `chore` | Build, dependency, or tooling changes |

### Golden Rules

- **Imperative mood:** "Fix the bug" ✅ — "Fixed the bug" ❌
- **Subject line ≤ 50 characters**
- **Explain the "why" in the body**, not the "what"
- **Atomic commits:** One logical change per commit
- **Subject starts with a capital letter and doesn't end with a period**

### Good Examples

```
feat(inventory): Add stock reservation endpoint

fix(shipping): Fix Aras Kargo tracking URL

chore(infra): Read etcd from environment variables
```