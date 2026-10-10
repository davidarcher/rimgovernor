# Working agreement

`AGENTS.md` is authoritative; `CLAUDE.md` points here. Start with the
[architecture](docs/developers/architecture/overview.md) and affected
[contracts](docs/developers/contracts/README.md). Machine setup belongs in the
[runbook](docs/developers/agent-runbook.md); checks belong in
[choose-tests](docs/developers/testing/choose-tests.md).

## The loop

1. Use a task branch in your own worktree. At session start, fetch `origin main`
   and merge `origin/main` once; local `main` may be behind.
2. Make one coherent milestone. From `go/`, run
   `go run ./cmd/test > ../test.out 2>&1` and wait for its final PASS/FAIL.
   Do not pipe it, rerun it to filter output, or follow it with `go test ./...`.
   Use `-full` only at an epic's end or for changed code covered by skipped tests.
   No native acceptance run is required before landing; the nightly supplies
   that evidence. C#/protobuf changes also require `task build` and `task test`.
3. Commit the milestone with checks and anything left unverified in the body.
   Make its subject the branch tip: the lane uses it for the squash title.
4. From `go/`, run `go run ./cmd/land` once, then fetch `origin main` and
   `git push origin main`. The lane locks, updates main, merges, squash-lands
   and resets the branch; it does not push. Do not combine `cmd/test` with
   `land -test`. A rejected push requires fetch, re-land and push, without
   retesting. Resolve lane-reported conflicts, then build; vet touched Go
   packages when Go files conflicted. Main moving is not a reason to rerun tests.
5. Continue authorized milestones. Close an issue when its stated acceptance
   is met; a registered, compiling acceptance case satisfies a case-writing
   task. Later nightly failures are separate issues.

Never open a PR, force-push, push a task branch, push a branch SHA to main, or
edit the main checkout. Details: [landing](docs/developers/development-process.md#landing).

## Architecture constraints

- RimWorld owns simulation and legality. Discover native schemas; editor and
  cheat operations stay outside model execution. Models use local LM Studio
  only, with no paid-provider fallback.
- Extend the shared Concern/Method/action system. Hands owns game-order writes;
  advisers neither write orders nor own colony invariants. Do not copy the
  existing combat bypass into new work.
- The only policy exception is [native rules](docs/developers/contracts/native-rules.md):
  Go authors pure policy, journals `rules_attach` before writing, and renews a
  lease each Round. Native executes only the closed whitelist and journals each
  firing before its write. No draft actions.
- Keep policy pure and orchestration free of decision math. Use typed boundary
  contracts, explicit ownership and explicit unknowns. The five mechanical
  [architecture gates](docs/developers/architecture/rules.md) have shrink-only
  baselines; never add exemptions to make a change pass.
- Before adding a fallback, retry, cache, wrapper or parallel path, check whether
  removing an earlier layer solves the problem. New behavior must trace to the
  task or issue; identify assumptions. Review orchestration, shared demand and
  unknown handling against rules 7–9.
- State has one owner: [persistence table](docs/developers/contracts/persistence-contracts.md).
  Amend that table before adding a store or second copy. Game intent lives in the
  save, the session journal in SQLite, derived state in memory, telemetry in
  `flight.jsonl`.
- Protobuf changes regenerate Go and C# together. Delete removed fields outright:
  no `reserved` fields/names and no compatibility shims.
- Use current vocabulary: Rounds inspect Concerns in Departments; Types are
  Standard, Project and Incident. Safeguards veto admission. See the
  [glossary](docs/developers/glossary.md).

## Evidence and diagnostics

- Test planner decisions with Go snapshots; native cases prove native reads,
  operations or simulation. An accepted order is not completed pawn work.
- New native cases start from a prepared fixture, normally `cases.LabStart()`;
  state why a snapshot test cannot prove the claim. Use a small quiet world,
  bounded progress waits and minute-scale budgets. Do not play for twenty
  minutes to create a precondition. See [case authoring](docs/developers/testing/acceptance-guide.md).
- Whole-module race/stress runs belong to the nightly. Concurrency fixes use
  focused race tests. Deadlines guard hangs, not latency; use synchronized
  events/game ticks for behavioral assertions. Do not rerun a full suite for a
  load-sensitive failure that passes alone; record it in the commit.
- `flight.jsonl` is the runtime log. Decisions use `telemetry.Decide` with
  `verdict`, stable `reason`, `target`, `dur_ms` and `attrs`. A new emission needs
  a registered [kind and reader](docs/developers/contracts/flight-rows.md).
  Log events and decisions, not per-step narration or unkinded `slog` text.

## Shared machine

- Stop only your game, by `acceptance stop -root <root>` or verified PID.
  Never kill `RimWorldWin64.exe` by image name.
- Build the mod through `acceptance setup -rebuild`, into your private
  `.rimgovernor/native-rimworld/`. Stop games using that copy first. Never
  replace DLLs in Steam or the shared isolated copy, or rebuild binaries a
  running harness uses.
- Keep the shared Go cache: no `go clean -cache` or private `GOCACHE`.
- Keep builds, logs, saves, databases and temporary scripts out of commits.
  Repository tooling is Go; no Python additions.
- Use absolute harness paths. Detached Windows processes use
  `Start-Process -WindowStyle Hidden`; do not use sleep/poll shell loops.
  Remote setup and game-free checks are in the [runbook](docs/developers/agent-runbook.md#remote-agents).

## Leave a maintainable result

Update the owning contract or player guide when behavior changes; link it from
the [documentation map](docs/README.md). Document current behavior, constraints
and useful rationale, not change diaries or duplicated source inventories.

Track out-of-scope bugs, deferred work and decisions in GitHub issues, one item
per issue. Check for an existing match first. Include concrete evidence and a
completion condition; use `priority:P0`/`P1`/`P2` or an applicable area label.
Do not file issues merely for unverified coverage. Read issues from `go/` with
`go run ./cmd/issue -last <n> <issue>` (flags first); remote agents use GitHub
MCP when the CLI is unavailable. Put ongoing issue status in comments.

Landing reports state the result, verification, rules 7–9 review and
`Complexity: added X / removed Y / deletion candidate: Z` (use `none` as needed).
