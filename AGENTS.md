# AGENTS.md

## Project Rules

This is a WinUI 3 C# serial monitoring application for long-running embedded device debugging.

Stability is more important than visual polish.

## Session Workflow and Commits (Required)

- Read [the development workflow](docs/development-workflow.md) before making repository changes.
- Treat each completed user-requested task as a commit boundary. Implement, verify, review the diff, and commit the task's changes before the final response. Do not defer completed changes to another session or a release.
- A long session may contain multiple commits for independently complete, verified changes. Keep each commit focused and usable; do not commit per file or save broken work just to meet a quota.
- The user's request to develop or fix something includes permission to make the corresponding local commits. Do not ask for commit confirmation unless the user explicitly requires it. An explicit request not to commit overrides this default.
- Before editing, inspect the current branch, HEAD, working tree, and staged diff. Record pre-existing changes so they are preserved and excluded from the current task's commit.
- Stage only the current task's files or hunks using explicit paths. Do not use `git add .`, `git add -A`, or `git commit -am`. Preserve unrelated staged changes as well as working-tree changes.
- If the current task builds on pre-existing uncommitted work, verify the actual commit's dependencies. Do not include that work implicitly or create a commit that only works with unrelated uncommitted files present.
- Run checks appropriate to the change and review `git diff --cached` before committing. Fix failures introduced by the task. Do not bypass hooks or claim unrun checks passed.
- After committing, inspect the commit and `git status --short`. Confirm all completed changes for this task are committed and unrelated changes are preserved. The whole working tree need not be clean when it was already dirty.
- In the final response, state what changed, the checks and their results, and the commit hash. If no commit was made, give the specific reason and describe any remaining changes; do not silently leave completed work uncommitted.
- Read-only work and tasks with no actual diff do not require an empty commit. If required verification is blocked or changes cannot be safely separated, report the blocker and remaining work rather than presenting the task as complete.
- Local commits do not authorize pushing, publishing, creating releases, or changing existing history. Do those only when requested. Do not amend, reset, stash, or discard unrelated work to obtain a clean tree.

## Architecture Rules

- Keep serial RX, parsing, logging, event detection, and UI rendering separated.
- Never update UI controls directly from the serial receive callback.
- Never write files directly from the serial receive callback.
- Use channels or queues between background components.
- Use batched UI updates.
- Keep only a bounded amount of logs in the UI.
- Full logs must be written to disk asynchronously.
- Handle background task exceptions and expose errors to the UI.

## Coding Style

- Prefer small classes with clear responsibilities.
- Prefer interfaces for services.
- Avoid putting business logic in MainWindow.xaml.cs.
- Use async/await carefully.
- CancellationToken must be supported for long-running tasks.
- Avoid blocking the UI thread.
- Avoid unbounded memory growth.

## Implementation Order

1. Create project skeleton and compile.
2. Implement mock serial data pipeline.
3. Implement real serial RX.
4. Implement file logging.
5. Implement bounded UI log buffer.
6. Implement TX command send.
7. Implement event detection.
8. Implement profile save/load.
9. Implement search/filter/highlight.
10. Implement long-running stability tests.
