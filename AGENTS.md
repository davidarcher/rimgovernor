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

- Start with the [documentation map](docs/README.md), [system overview](docs/explanation/overview.md),
  [backlog](docs/BACKLOG.md) and [test selection](docs/how-to/choose-tests.md).
  Read the linked explanation and reference for the subsystem being changed;
  use task-specific how-to guides for commands. Runtime: Python, React,
  GABS/RimBridgeServer and `integrations/colony-bridge`.
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

- Start with the [test selection guide](docs/how-to/choose-tests.md).
  [Docker controller checks](docs/how-to/docker-checks.md)
  need no game files or model server; the separate native Docker runner needs
  licensed Linux inputs. Its current assertions cover lifecycle and optional
  rendered/input behavior; native pawn outcomes require scenario assertions.
  Use fresh output directories and task-specific image tags; retain reports and
  failures under `.rimbot/` and report the exact scope tested.
- During iteration, run affected test files and their contract neighbors. Run the
  full affected suite once before handoff; avoid repeating it on an unchanged revision.
  Use one Docker check worker by default: extra workers repeat, not shard, tests.
  Use `--controller-only --test controller_tests/test_NAME.py` for focused Docker
  controller checks; omit `--test` for the full controller suite.
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
- Organize docs by purpose: tutorials teach through an exercise, how-to guides
  solve a task, reference states exact contracts, and explanation develops the
  reader's understanding. Keep the [documentation map](docs/README.md) current.
- Keep all unfinished implementation, audit and acceptance work in
  `docs/BACKLOG.md`. Update architecture and procedures when behavior changes.
- Put iteration evidence in commit messages and generated test artifacts. Preserve
  required license/provenance records beside reused source.
- `AGENTS.md` is the source of these instructions; `CLAUDE.md` is a symlink to it.
