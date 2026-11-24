# Working notes

How the work on NextCommander is done with Claude Code, kept in the repository so it is the same on every machine (the
assistant's own memory stays on the machine it was written on). The open work is in
[remaining-work.md](remaining-work.md); the project's structure and rules in [CLAUDE.md](../CLAUDE.md).

## The user's short requests (Turkish)

| Request | What is done |
|---|---|
| **kalan işler** / kalan işler? | The open `- [ ]` items of remaining-work.md, grouped: to try on a real machine, blocked or unused in code, feature ideas waiting for a decision, AI ideas; with one suggestion for what comes next. |
| **commit sürecini ilerlet** | The whole chain: tests (below), commit on the work branch, `--no-ff` into `development`, push `development` and the branch (`-u`), then `development` into `main` (below), push `main`, back on `development`. |
| **testleri durdur ve commit sürecini ilerlet** | Stop the running test run and go on with the commit chain at once. |
| **herşeyi kaydet** | Everything committed and pushed; the notes needed on another machine in the repository (this file, remaining-work.md, CLAUDE.md). |

## Git

The rules of [rules/feedback_git_workflow.md](../rules/feedback_git_workflow.md) and
[rules/feedback_branch_push.md](../rules/feedback_branch_push.md):

- A branch from an up-to-date `development` for every piece of work: `feature/…`, `fix/…`, `docs/…`.
- Conventional Commits: `feat(scope): Imperative capitalized subject` (at most 50 characters, no period); the body says
  why; the last line `Co-Authored-By: Claude …`.
- `git checkout development && git pull && git merge --no-ff <branch>`, push `development`, `git push -u origin <branch>`.
  Branches are not deleted; nothing is force-pushed.
- `main`: since 23 November 2026 `development` goes into `main` with a merge commit, as other sessions did:
  `git checkout main && git reset --keep origin/main && git merge --no-ff development -m "Merge branch 'development'"`,
  push `main`. A local `main` behind or apart from `origin/main` is normal (origin's history was rewritten once): check
  that `origin/main` has nothing of its own (`git diff origin/main <its development parent>` is empty), never force.
- Commit only when the user asks.

## Tests before a commit

- The full App suite takes about 20 minutes; the user stopped it twice. For "commit sürecini ilerlet": `dotnet build`
  of everything (no warnings), the **full Core suite**, and the **App test classes the change touches** (always
  `MenuTests` and `UserFilesTests` when menus or texts changed, plus the new tests). Say that the full App suite was not
  run; run it when the user asks.
- `NativeAssetsTests` fails when the build goes to a folder outside the repository (it looks for the repository root):
  not a problem of the code.
- Timing tests can fail under the full run and pass alone; run the class alone before changing anything.

## While the program runs from bin/Debug

The user often keeps NextCommander open from `src/NextCommander.App/bin/Debug`: the build cannot copy over its DLLs.
Never stop the program; build and test into a folder of your own:

```sh
dotnet build --artifacts-path <scratch folder>/artifacts
dotnet test tests/NextCommander.Core.Tests --artifacts-path <scratch folder>/artifacts --no-build
```

## New texts

Every new UI text needs a key in the six language files (en, tr, de, es, fr, zh); plugins keep theirs in their own
`languages` folder. `python build/extract_strings.py <out.json>` lists the texts of the code missing from en.json;
write the keys with their six texts into a JSON file and add them with
`python build/add_l10n_keys.py <keys.json> src/NextCommander.App/Localization`. Texts put together from pieces cannot
be translated as a whole: one interpolated pattern per message (`$"... {count} ..."`), separate patterns for one and
for many, or `Localizer.T(pattern)` for each piece.

## Pitfalls met in these sessions

- Shell heredocs mangle `\n`, `\\` and quotes in C# and JSON: edit with the editor tools, or write a Python patch file
  first and run it (see CLAUDE.md, Known pitfalls). A `cat` with no input in a command waits for ever.
- A domain-joined Windows without its domain throws `Win32Exception` ("the trust relationship ... failed") when a name
  is looked up (`NTAccount.Translate`).
- The local test servers (`tests/servers`) need their Python packages; for one server a venv of its own is enough
  (e.g. `asyncssh` for `sftp_server.py`, then `FM_TEST_SFTP=sftp://tester:%C5%9Fifre123@127.0.0.1:2222/`).

## State on 24 November 2026

- `main` = `development`, everything pushed.
- Waiting for the user on the Mac: does the program open (start fix of 24 November), does the Samsung phone open over
  MTP (the libmtp search fix of 24 November). If not: the outputs listed in remaining-work.md.
- Suggested next: "Media and documents" of the feature ideas (batch picture operations, picture compare) on the
  program's SkiaSharp.
