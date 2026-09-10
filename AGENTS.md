# Working agreement

## Work safely together

- Use a separate Git worktree and task branch when other agents or developers may
  be working in the repository. Check status before editing; preserve their work.
- Keep changes scoped to the task. Coordinate shared-file changes and integration;
  do not merge into an actively edited checkout without coordination.
- Commit each completed iteration after relevant checks. Local checkpoint commits
  are authorized; do not ask again. Report the branch and commit hash. Do not push
  unless requested.
- Keep generated builds, logs, saves, databases and temporary scripts out of commits.

## Architecture and implementation

- Read [architecture](docs/ARCHITECTURE.md), the [backlog](docs/BACKLOG.md) and
  [testing runbook](docs/TESTING.md). Runtime: Python, React, GABS/RimBridgeServer
  and `integrations/colony-bridge`.
- Keep one shared goal/action system and deterministic Hands. Routine control is
  deterministic; player chat interprets explicit semantic requests. Advisers cannot
  write game orders or own colony invariants.
- Preserve normal game rules. Discover native schemas and definitions; keep
  editor/cheat operations outside model execution. RimWorld owns simulation.
- Use configured local LM Studio models with no silent paid-provider fallback.
- Verify native outcomes: receipts do not prove pawn work completed. Observe
  uncertain writes before retrying. Plans do not own arbitrary map coordinates.
- Manual, player direction and colony/load/map changes invalidate pending work.
- Preserve UI drafts and last good data during background refreshes.

## Validation

- Start with the [test selection guide](docs/TESTING.md#choose-the-test-scope).
  [Docker controller checks](docs/TESTING.md#docker-controller-checks-no-game-required)
  need no game files or model server; the separate native Docker runner needs
  licensed Linux inputs and verifies game lifecycle, not completed pawn work.
  Use fresh output directories and task-specific image tags; retain reports and
  failures under `.rimbot/` and report the exact scope tested.
- Use checks appropriate to the change; `build.ps1` runs controller and dashboard
  checks. Distinguish compilation/protocol checks from actual gameplay validation.
- Never replace installed DLLs while any RimWorld instance is running, including
  another worktree's tests. Isolated tests must restore temporarily swapped DLLs.
- Preserve source attribution in [THIRD_PARTY.md](THIRD_PARTY.md) and the integration
  provenance files. Native changes need game-level acceptance.

## Documentation and comments

- Keep prose and code comments concise and forward-looking. Explain current
  behavior, contracts, constraints and useful rationale; no design archeology,
  chronological implementation diaries or accounts of superseded approaches.
- Keep all unfinished implementation, audit and acceptance work in
  `docs/BACKLOG.md`. Update architecture and procedures when behavior changes.
- Put iteration evidence in commit messages and generated test artifacts. Preserve
  required license/provenance records beside reused source.
- `AGENTS.md` is the source of these instructions; `CLAUDE.md` is a symlink to it.
