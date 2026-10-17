# Working agreement

`AGENTS.md` is authoritative; `CLAUDE.md` points here. Start with the
[architecture](docs/developers/architecture/overview.md) and affected
[contracts](docs/developers/contracts/README.md). Machine setup belongs in the
[runbook](docs/developers/agent-runbook.md); checks belong in
[choose-tests](docs/developers/testing/choose-tests.md).

## The loop

1. Use a task branch in your own worktree. At session start, fetch `origin main`
   and merge `origin/main` once; local `main` may be behind.
2. Make one coherent milestone. From the worktree root, run
   `RimGovernor.cmd test > test.out 2>&1` and wait for its final PASS/FAIL.
   Do not pipe it, rerun it to filter output, or follow it with your own `dotnet build`/`dotnet test`; it runs the gate when C# changed.
   Use `-full` only at an epic's end or for changed code covered by skipped tests.
   No native acceptance run is required before landing; the nightly supplies
   that evidence. C# changes are gated by `RimGovernor.slnx`, the contract probes and the native build, which `test` runs for you.
3. Commit the milestone with checks and anything left unverified in the body.
   Make its subject the branch tip: the lane uses it for the squash title.
4. From the worktree root, run `RimGovernor.cmd land` once. The lane locks,
   updates main from origin, merges, squashes, pushes the squash to origin's
   main (landing again on top when origin moved, without retesting), then moves
   local main and the main checkout and resets the branch. Do not combine
   `test` with `land -test`. Resolve lane-reported conflicts, then build.
   Main moving is not a reason to rerun tests.
5. Continue authorized milestones. Close an issue when its stated acceptance
   is met; a registered, compiling acceptance case satisfies a case-writing
   task. Later nightly failures are separate issues.

Never open a PR, force-push, push a task branch, push to main yourself (the lane
pushes), or edit the main checkout. Details: [landing](docs/developers/development-process.md#landing).

## Architecture constraints

- One C# toolchain (.NET SDK 10). The brain is a library (`brain/`) hosted inside the
  game process by the mod (`-rimgovernor-inproc`, the default launch): no controller
  process, no transport. The launcher (`tools/launcher`), the tool host
  (`RimGovernor.cmd <tool>`: the front door `tools/repo`, which hands brain tools to `tools/rg`) and the acceptance harness
  (`RimGovernor.cmd acceptance`, `brain/src/RimGovernor.Brain.Acceptance*`) are C# too.
  See [brain host](docs/developers/architecture/brain-host.md).

- RimWorld owns simulation and legality. Discover native schemas; editor and
  cheat operations stay outside model execution. Models use local LM Studio
  only, with no paid-provider fallback.
- Extend the shared Concern/Method/action system. Hands owns game-order writes;
  advisers neither write orders nor own colony invariants. Do not copy the
  existing combat bypass into new work.
- Keep policy pure and orchestration free of decision math. Use typed boundary
  contracts, explicit ownership and explicit unknowns. The five mechanical
  [architecture gates](docs/developers/architecture/rules.md) have shrink-only
  baselines; never add exemptions to make a change pass.
- Reference game and repo symbols in ways the compiler checks. Use `DefOf`
  fields, `nameof`, typed members or a typed path such as
  `WorkGiverDefOf.DoBillsCook.workType`, not `GetNamed("Cooking")`, string
  reflection or hand-typed def names. If a `DefOf` field is missing, look for
  a typed route to the def first. Use a string lookup only when no typed route
  exists, and say so in a comment.
- Before adding a fallback, retry, cache, wrapper or parallel path, check whether
  removing an earlier layer solves the problem. New behavior must trace to the
  task or issue; identify assumptions. Review orchestration, shared demand and
  unknown handling against rules 7–9.
- State has one owner: [persistence table](docs/developers/contracts/persistence-contracts.md).
  Amend that table before adding a store or second copy. Game intent lives in the
  save, the session journal in SQLite, derived state in memory, telemetry in
  the OTLP files.
- Wire changes edit the records in `brain/src/RimGovernor.Brain.Contracts` (the C# source of truth since #3100);
  Delete removed fields outright:
  no `reserved` fields/names and no compatibility shims.
- Use current vocabulary: Rounds inspect Concerns in Departments; Types are
  Standard, Project and Incident. Safeguards veto admission. See the
  [glossary](docs/developers/glossary.md).

## Evidence and diagnostics

- Prove planner decisions with a bug-driven xUnit test in `brain/tests` where one is
  needed. Native acceptance cases follow the
  [acceptance charter](docs/developers/testing/acceptance-guide.md#charter): one native
  claim, a staged precondition, a tick budget, typed reads and telemetry by kind constant.
- Whole-module race/stress runs belong to the nightly. Concurrency fixes use
  focused race tests. Deadlines guard hangs, not latency; use synchronized
  events/game ticks for behavioral assertions. Do not rerun a full suite for a
  load-sensitive failure that passes alone; record it in the commit.
- Real-game combat sweeps cost minutes per seed. To compare a change, run one seed
  (three at most) against the same seed in the committed baseline, and widen in
  the sim. Run a full 20-seed sweep, a baseline re-record or a variant set only
  when the user asks or the nightly does; never as a default step of a fix, and
  never fixture after fixture in a row.
- The OTLP log ring (`otlp/logs.jsonl`) is the runtime log. Decisions use `TelemetryEmitter.Decide` with
  `verdict`, stable `reason`, `target`, `dur_ms` and `attrs`. A new emission needs
  a registered [kind and reader](docs/developers/contracts/otel-signals.md).
  Log events and decisions, not per-step narration or unkinded `slog` text.

## Shared machine

- Stop only your game, by `acceptance stop -root <root>` or verified PID.
  Never kill `RimWorld.exe` by image name.
- Build the mod through `acceptance setup -rebuild`, into your private
  `.rimgovernor/native-rimworld/`. Stop games using that copy first. Never
  replace DLLs in Steam or the shared isolated copy, or rebuild binaries a
  running harness uses.
- Keep builds, logs, saves, databases and temporary scripts out of commits.
  Repository tooling is C# (`tools/repo`, `tools/rg`); no Python additions. For one-off
  scripting, use PowerShell or the repo's existing tools.
- Use absolute harness paths. Detached Windows processes use
  `Start-Process -WindowStyle Hidden`; do not use sleep/poll shell loops.
  Remote setup and game-free checks are in the [runbook](docs/developers/agent-runbook.md#remote-agents).

## Leave a maintainable result

Update the owning contract or player guide when behavior changes; link it from
the [documentation map](docs/README.md). Document current behavior, constraints
and useful rationale, not change diaries or duplicated source inventories.

Code your change makes unused is part of your change. Before committing, find
every symbol the diff stopped reading (fields, records, native reads, wire
contracts, helpers, tests, docs). Delete each one that has no remaining reader
through every layer in the same commit. The native read or wire boundary is
not a reason to stop. Leave an orphan only if the user explicitly limited the
scope. In that case, file an issue for it and link it; a mention in the report
does not count.

Track out-of-scope bugs, deferred work and decisions in GitHub issues, one item
per issue. Check for an existing match first. Include concrete evidence and a
completion condition; use `priority:P0`/`P1`/`P2` or an applicable area label.
Do not file issues merely for unverified coverage. Read issues from the worktree root with
`RimGovernor.cmd issue -last <n> <issue>` (flags first); remote agents use GitHub
MCP when the CLI is unavailable. Put ongoing issue status in comments.

Landing reports state the result, verification, rules 7–9 review and
`Complexity: added X / removed Y / orphans left: none` (or list each
`<symbol> — #issue` when the user limited the scope).
