# Working agreement

## Work safely together

- Use a separate Git worktree and task branch when other agents or developers may
  be working in the repository. Check status before editing; preserve their work.
- Keep changes scoped to the task. Coordinate shared-file changes and integration;
  do not merge into an actively edited checkout without coordination.
- Commit each completed iteration after relevant checks. Local checkpoint commits
  are authorized; do not ask again. Report the branch and commit hash. Landing on
  main includes syncing and pushing origin/main; no separate push approval is needed.
- Keep generated builds, logs, saves, databases and temporary scripts out of commits.

## Delivery speed and coordination

- Define a completion criterion sized to a coherent milestone, not the smallest
  possible increment. Game and acceptance checks are slow: batch the related
  steps that share a milestone into one iteration so verification runs once
  against meaningful progress, instead of once per trivial edit. Once checks
  pass, commit and deliver; put unrelated discoveries in the backlog. Continue
  to the next coherent milestone of an authorized task without waiting to be
  re-prompted.
- Default to one agent. Use requested teams for independent, bounded work. Agree
  once on file/component ownership, shared contracts and one integration owner,
  then work independently. Do not narrate edits to peers or ask for speculative
  conflict checks before each change or merge.
- Communicate only actual ownership overlap, contract changes, blockers needing
  a decision, or a ready handoff. Batch questions; hand off a commit, affected
  paths and concise verification evidence. No acknowledgment loops, status
  polling, relay chains or coordination-only agents without a concrete need.
- When landing is authorized, stream small verified commits into main as they
  become ready. Assume main is continuously updated: validate the task and merge;
  do not chase each new HEAD with a rebase/retest cycle. Do not wait for unrelated
  teams or require a global quiet period.
  Check the target checkout and diff locally; coordinate only actual overlap or
  an actively edited target. Resolve routine integration locally.
- Before each landing, fetch origin and pull origin/main into the clean main
  checkout (`git pull --ff-only origin main`), then integrate the verified task
  commits and push main (`git push origin main`). If main has diverged, merge
  origin/main without discarding either side and check any resolved conflicts.
  If the push loses a race, fetch and integrate the new remote commits, then retry.
  Never force-push main. For an empty remote, the first landing uses
  `git push -u origin main`. A landing is complete only when origin/main contains
  the landed commits; report any authentication or network blocker.
- Test evidence follows relevant code, dependencies, inputs and environment, not
  the main HEAD hash. Unrelated main commits, clean cherry-picks and rebases do
  not invalidate passing results. Rerun only checks affected by changed behavior,
  dependencies or conflict resolution. Reuse other agents' applicable evidence.


## Architecture and implementation

- Follow the [development process](docs/developers/development-process.md): verified
  slices sized to a milestone rather than the smallest possible step, strict typed
  contracts and explicit component ownership. Integrate features through existing
  architecture; keep unstructured data at validated boundaries.
- Start with the [documentation map](docs/README.md), [system overview](docs/developers/architecture/overview.md),
  the [backlog issues](https://github.com/davidarcher/rimgovernor/issues) and
  [test selection](docs/developers/testing/choose-tests.md).
  Read the component guide and contracts for the subsystem being changed;
  use its testing page for commands. Runtime: Python, React,
  GABS/RimBridgeServer and `integrations/rimgovernor-native`.
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

- Before reporting missing native test inputs, read [local acceptance inputs](docs/developers/testing/local-acceptance-inputs.md).
  The shared Windows/Linux store is `.rimgovernor/acceptance-inputs` under the
  primary RimGovernor checkout; resolve it through Git common-dir from worktrees.
  Copy private run inputs and build task DLLs there.

- Follow the testing pyramid: many fast unit tests, fewer integration tests and
  a small set of targeted game acceptance scenarios. Keep the edit/test loop fast.
  Use fixtures, replay and contract tests for most migration parity; parameterize
  biome and colony policy variants where simulation is unnecessary.
- Before a slow check, identify the changed behavior or unresolved failure it
  verifies and why cheaper checks are insufficient. Run targeted game acceptance
  at relevant feature milestones, not after every edit. Broad scenario matrices
  and sustained campaigns are separate scheduled or explicitly requested work.
- After an acceptance failure, add a fast regression test where feasible and
  rerun the affected scenario. Do not restart a whole campaign without a specific
  reason. Do not duplicate tests or reviews already supported by applicable evidence.
- Native scenario tick waits use `rimgovernor.native_scenario.advance_game` instead of
  bespoke start/poll/resume loops. Its default acknowledges inspected Ancient danger
  fixture warnings and preserves the remaining tick budget. Interruption acceptance
  passes `expected_letters=()`; unexpected stops remain failures with evidence.
- Start with the [test selection guide](docs/developers/testing/choose-tests.md).
  [Docker controller checks](docs/developers/testing/docker-checks.md)
  need no game files or model server; the separate native Docker runner needs
  licensed Linux inputs. Its current assertions cover lifecycle and optional
  rendered/input behavior; native pawn outcomes require scenario assertions.
  Use fresh output directories and task-specific image tags; retain reports and
  failures under `.rimgovernor/` and report the exact scope tested.
- Use `scripts/container_scenario.py` for new script-based native Docker runs.
  Specialized launchers must use its `dashboard_options` and
  `require_dashboard_image` helpers so scenarios publish automatic loopback
  dashboard ports. Rebuild old images; do not silently omit observation support.
- During iteration, run affected test files and their contract neighbors. Run the
  full affected suite once before handoff; reuse results while relevant inputs
  remain unchanged, even when main advances.
  Use one Docker check worker by default: extra workers repeat, not shard, tests.
  Use `--controller-only --test controller_tests/test_NAME.py` for focused Docker
  controller checks; omit `--test` for the full controller suite.
- Use checks appropriate to the change; `build.ps1` runs controller and dashboard
  checks. Distinguish compilation/protocol checks from actual gameplay validation.
- Never replace installed DLLs while any RimWorld instance is running, including
  another worktree's tests. Isolated tests must restore temporarily swapped DLLs.
- Native behavior changes need targeted game-level acceptance before completion.
  Documentation-only edits need no game session. The full affected suite means
  the applicable automated suite, not the entire gameplay scenario matrix.

## Documentation and comments

- Keep prose and code comments concise and forward-looking. Explain current
  behavior, contracts, constraints and useful rationale; no design archeology,
  chronological implementation diaries or accounts of superseded approaches.
- Organize docs around players and developers. Give the reader the behavior,
  commands or contracts needed for their task; link to detail and avoid repeating
  shared rules. Keep the [documentation map](docs/README.md) current.
- Keep all unfinished implementation, audit and acceptance work tracked as
  [GitHub issues](https://github.com/davidarcher/rimgovernor/issues), labeled
  by priority (`priority:P0`/`P1`/`P2`) or rewrite area (`area:G01`/`N01`/`tooling`).
  Update architecture and procedures when behavior changes.
- Put iteration evidence in commit messages and generated test artifacts. Keep
  applicable source and dependency notices beside retained code, without change diaries.
- `AGENTS.md` is the source of these instructions; `CLAUDE.md` is a symlink to it.
