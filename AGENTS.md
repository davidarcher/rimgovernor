# Working agreement

`AGENTS.md` is the source; `CLAUDE.md` is a symlink to it. Machine setup (game
copy, mod build, harnesses) is the [agent runbook](docs/developers/agent-runbook.md);
which checks a change needs is [choose-tests](docs/developers/testing/choose-tests.md).

## The loop

Take the shortest valid path: run only the required checks, stop exploring once
acceptance passes, land immediately.

1. Work on a task branch in your own worktree; run `git fetch origin main &&
   git merge origin/main` once at session start (local `main` may be behind:
   every agent pushes `origin/main` directly).
2. Edit; `go run ./cmd/test` from `go/` is the test loop. It tests `./...` (the
   Go test cache replays unchanged packages), passes `-short` (skips slow
   git/planner/solver tests, ~10 s warm) and
   names the acceptance harnesses the change touches. Use `-full` only at the
   end of an epic or when you changed code a skipped test covers. Do not follow
   it with `go test ./...` or rerun it to filter output. Redirect it to a file
   (`go run ./cmd/test > ../test.out 2>&1`), never pipe it, and wait for the
   final `test: PASS (...)` / `test: FAIL (...)` line without polling.
3. Commit each completed iteration (checkpoint commits are authorized). Size an
   iteration to a coherent milestone so slow checks run once.
4. No acceptance run before landing; the required check is one `cmd/test`. The
   nightly (a signal, not a gate) proves the rest
   ([choose-tests](docs/developers/testing/choose-tests.md)).
5. `go run ./cmd/land` from the branch worktree,
   never piped (redirect if you need the output). The lane titles the squash
   with the branch tip's commit subject, so make the milestone commit the tip
   and fold fixups into it first. It takes the repository lock, fast-forwards
   local `main` to `origin/main` (refusing a diverged `main`), merges `main`
   into the branch, squash-lands on the
   `main` checkout, resets the branch to `main`, and closes the branch's issue
   (only when the branch is `issue-<n>-...` or you pass `-issue <n>`). It does
   not push. `-test` runs the same `-short` tests as `cmd/test`; never run both.
   Call it once; land each ready milestone rather than holding a branch. Merge
   by hand only to resolve a conflict it reports.
   An orchestrator removes each agent's worktree once the agent reports
   (`git worktree remove <path>`, then `git branch -D <branch>` if nothing is
   off `main`); the harness locks a running agent's worktree.
   An issue closes when the landing meets the acceptance in its body, not when
   every follow-up is done: comment follow-ups in one line (or file issues). An
   issue left open with a "remaining:" paragraph reads as blocked.
6. Continue to the next milestone of an authorized task without being
   re-prompted; stop only on a blocker you cannot clear, and name it (commit,
   issue number, decision needed). Do not post readiness checks or assessments
   that change nothing. An issue whose acceptance is an acceptance case is
   finished once the case is written, registered and builds clean; a failure in
   the next nightly opens a new issue.

`main` moves constantly; that is never a reason to redo anything. A test that
passed on the branch's code stays passed after the lane's merge, and a second
rerun-and-land cycle per milestone is forbidden. After resolving a conflict,
run `go build ./...` (plus `go vet` on touched packages when Go files
conflicted), not the tests. A test that fails under load and passes alone is
not a reason to rerun: land and say so in the commit body. Say what is left
unverified in the commit body; do not open an issue for it (the next full-suite
pass verifies it).

## Pushing to origin/main

Local agents (maintainer's Windows machine) and remote agents (cloud sessions
in a fresh Linux clone, no game) all land against GitHub's `main`, and the
maintainer pushes there too. Every agent:

1. Branches from a freshly fetched `origin/main`.
2. Lands with `go run ./cmd/land`.
3. Runs `git fetch origin main`, then `git push origin main` immediately. If
   rejected, fetch and run `go run ./cmd/land` again (it re-lands the branch;
   nothing is retested), then push. Never force-push `main` or push a branch
   SHA to `main` (`git push origin <sha>:main`): that skips the squash and
   issue close.

Remote agents additionally:

1. Run no acceptance; put `Unverified: no acceptance run (remote agent)` in the
   commit body. The nightly run verifies it.
2. Compile-check C# with `scripts/build_native_ref.sh` (fixture switches pass
   through, e.g. `-p:UpkeepFixture=true`); it builds against public NuGet
   reference assemblies, never the game. It needs
   `apt-get install -y dotnet-sdk-8.0` (Microsoft's installer host is blocked
   by the cloud proxy).
3. Use the GitHub MCP tools for everything on GitHub (`gh` and GitHub GraphQL
   are blocked in Cloud sessions, so `go run ./cmd/issue` fails too; load the
   tools with ToolSearch if they are deferred). Read an issue with
   `mcp__github__issue_read` (`get`, `get_comments`, `get_sub_issues`), find one
   with `search_issues` or `list_issues`, file or edit with `issue_write`, comment
   with `add_issue_comment`. Scope is `davidarcher/rimgovernor`. `cmd/land`
   reports a failed issue close without failing: close the issue yourself with
   `issue_write` (`state: closed`, `state_reason: completed`) and a one-line
   comment naming the landing commit. Pull requests stay disabled.

## Simplify before you extend

Before adding a layer (flag, fallback, retry, cache, special case, wrapper,
parallel path), check whether an earlier layer that should not exist causes the
problem; if so, propose removing it. Cite the issue text or user message that
asks for new behaviour; otherwise it is an assumption: flag it. When a second
fix for one symptom would add another guard, explain why the first failed.
Every landing report ends with
`Complexity: added X / removed Y / deletion candidate: Z` (or `none`).

## Never

- Open a pull request (disabled), push any branch but `main`, or force-push.
- `git reset --soft main` to squash, or edit the `main` checkout directly (the
  user's launcher game builds from it): land, then restart the launcher.
- Kill `RimWorldWin64.exe` by image name; peers' games run beside yours. Stop
  your own by root or pid (runbook).
- Replace a DLL under a game install a running RimWorld uses. Your worktree's
  mod lives in its private `.rimgovernor/native-rimworld/` copy, so peers' games
  and the launcher game never block `acceptance setup -rebuild`; stop your own
  game first (`acceptance stop -root <root>`). The Steam install and the shared
  `.rimgovernor/isolated-rimworld` copy are never yours to replace.
- `go clean -cache`, or set a private `GOCACHE`.
- Rebuild the controller binary or the mod while a harness runs from them.
- Commit generated builds, logs, saves, databases or temporary scripts.
- Add Python to the repository. Tooling, acceptance cases and analysis are Go
  (`go/cmd`, `go/internal/nativeaccept/cases/<area>`); throwaway Python stays in
  the scratchpad.

## Tool pitfalls (Windows harness)

- Build the mod through `acceptance setup` (`-rebuild`, `-fixture A,B`), never
  `scripts/build_native_mod.ps1` by hand: the harness refuses PowerShell
  commands carrying a `C:\Program Files` argument.
- Never `sleep N && <check>` or start background timers/poll loops to wait on a
  run or agent; the harness blocks or re-invokes on them. Launch long commands
  with `run_in_background: true` and wait for the notification (or `Monitor`
  with an until-loop); otherwise report status and end the turn.
- Write files with the Write tool, not Bash heredocs (the Bash tool re-escapes
  apostrophes and backslashes).
- `python`, not `python3` (the Microsoft Store stub).
- Read issues with `go run ./cmd/issue <n>` from `go/` (body and comments in
  one call, also written to `issue-<n>.md`; `-last k` for the newest comments).
  Not `gh issue view --comments`, never WebFetch a github.com URL. (Cloud
  sessions: the GitHub MCP tools, see Remote agents above.)

## Issues

Open a GitHub issue (`gh issue create`; `issue_write` in a Cloud session) for anything you would otherwise leave
as "follow-up" or ask about in a summary: bugs found in passing, deferred scope,
decisions needed, and what you notice but are not fixing (slow test, senseless
workflow step, performance or architecture smell). Not for an unverified
landing. One issue per item: terse title, concrete evidence (file, commit, log
line), what would resolve it, labeled `priority:P0`/`P1`/`P2` or
`area:G01`/`N01`/`tooling`. Check open issues first and comment on a match.
Cite numbers instead of restating. Status goes in issue comments, not chat: a
sentence on what landed or remains and the commit.

## Checks

- Whole-module race checks and repeated race stress runs are nightly work; do
  not run them locally unless asked. For a concurrency fix use a focused
  `go test -race -run <tests>`; repeat only those tests to reproduce a flake.
  Prefer synchronized events or virtual time over wall-clock bounds.
- Test deadlines are hang guards, not latency assertions. A
  `context deadline exceeded` or `checktesttimes` breach under load measured the
  machine: widen the guard to the accepted maximum (bridge `testBudget`,
  `playerFixture` CallTimeout, the 60 s `checktesttimes` guard) or assert the
  event. Never tighten a deadline per test, shave a test to fit a budget, or
  open a per-test timing issue.
- Pyramid: many fast Go unit tests (`cmd/test`), fewer integration tests, a
  small set of native acceptance cases run by
  `go/internal/nativeaccept/cmd/acceptance` (`acceptance run <area>/<case>`,
  `acceptance suite`) against a real headless RimWorld. Before a slow check, say
  what changed behaviour it verifies and why a cheaper check is insufficient.
- `task build && task test` runs every project's gates (protobuf, C#); use it
  when the change touches those, not for Go-only work.
- A receipt does not prove pawn work completed: assert the native
  postcondition. Distinguish compilation/protocol checks from gameplay
  validation.
- After an acceptance failure, add a fast regression test where feasible and
  rerun the case. The rerun resumes from the last checkpoint by default;
  `-fresh` starts over (the land suite runs fresh unless `-resume`).
  `-postmortem-only` reloads the failed bundle and runs only `Postmortem` (for
  edits to a case's reads or asserts); `acceptance dev <case>` rebuilds
  `rimgovernor` and reruns `Run` and `Postmortem` on the kept process (for
  edits to the code a late stage exercises).
- Native performance work iterates with `acceptance profile-capture -root <dir>`
  (flags `-from` incl. `latest-ci`, `-case`, `-save`, `-n`, `-equality`), not a
  sustained run: it times `SnapshotFrames.Capture` paused on the newest
  `sustained/colony` checkpoint and prints p50/p90/max per family.
- A new case starts from a fixture that already exercises the behaviour (a
  baseline save, a `test/*_prepare` op, or a programmatic start) and follows
  the performance checklist in choose-tests: Core-only, quiet storyteller,
  stall-bounded waits, minute-scale budgets. Playing a colony into its
  precondition for 20 minutes is a fixture bug.
- A new fixture case starts on `cases.LabStart()` (blank 100x100 lab) and spawns
  what it needs at known offsets from the centre; it never searches a random
  world. `TestFixtureCasesStartPinned` refuses an unpinned random start unless
  the case is on its exemption list with a terrain reason.
- Snapshot first: a new native case states in its scope text why a Go snapshot
  test over recorded colony facts cannot cover it (native op or read contract,
  end-to-end signal, vanilla physics). A planner decision is a snapshot test,
  not a case; a failing planner-decision case is replaced by a snapshot test and
  deregistered, not repaired.
- Trust `go/internal/nativeaccept/cmd/` over checks named in old commits or
  issues.

## Architecture

Start with the [documentation map](docs/README.md), the
[system overview](docs/developers/architecture/overview.md) and the
[development process](docs/developers/development-process.md); read the
component guide and contracts for the subsystem you change. Runtime: Go (`go/`,
including the launcher), the GABP host (`integrations/rimgovernor-host`, over GABP) and
`integrations/rimgovernor-native`.

Non-negotiables: RimWorld owns simulation and normal game rules hold (discover
native schemas; editor/cheat operations stay outside model execution). One
shared Concern/action system with deterministic Hands: Hands are the only writer
of game orders, except declarative [native rules](docs/developers/contracts/native-rules.md),
which Go authors (a pure policy function), journals before the write (the
`rules_attach` action has a receipt and sits in the session journal first) and
leases (renewed each Round; native deactivates them when the lease lapses), and
native only executes within a closed whitelist (v1: `PREY_KILLED` then
`give_job` Hunt; no draft actions). Every firing is journaled on the clock ring
before its write. Advisers never write game orders or own colony invariants.
Local LM Studio models only, no silent
paid-provider fallback. Typed contracts at boundaries; explicit component
ownership; integrate through the existing architecture. State placement is one
table in [persistence contracts](docs/developers/contracts/persistence-contracts.md)
(Go intent in the save, session journal in SQLite, derived state in memory,
telemetry in `flight.jsonl`); a second copy of a fact or a new store amends that
table first. Manual control: [control loop guide](docs/developers/architecture/control-loop.md#manual-control).

## Logging

`flight.jsonl` is the only log ([flight rows](docs/developers/contracts/flight-rows.md),
schema v2, epic #2038). Log a thing that happened (a planner ran, a method was
chosen, a window was admitted or refused, an action was dispatched, a call
finished), not a trace of how the code got there: no per-step chatter, no
"entering X". A decision is one row in the fixed shape `verdict`, `reason`,
`target`, `dur_ms` plus an `attrs` object, emitted with `telemetry.Decide`.
Add rows, not free text: put the data in `attrs`, keep `reason` a stable word so
repeats collapse, and never format a sentence. A fresh emission needs a kind in
the flight-rows table and a reader that uses it, or it is deleted; an unkinded
`slog` call is not a way to log.

## Docs and comments

Concise and forward-looking: current behaviour, contracts, constraints, useful
rationale. No design archaeology, implementation diaries or superseded
approaches, in prose or commit messages. Organise for players and developers;
link rather than repeat; keep the [documentation map](docs/README.md) current
and update architecture and procedure docs when behaviour changes.

## Vocabulary (epic #1964)

The governor makes **Rounds**, running an **Inspection** on each **Concern** in
its **Department**; a Concern takes one **Type**: **Standard**, **Project** or
**Incident**. The rename of
[#1964](https://github.com/davidarcher/rimgovernor/issues/1964) is complete:
code, storage and logs use the new words, and the table maps the old words for
grep and history. Per-rename status:
[agent runbook glossary](docs/developers/agent-runbook.md#vocabulary-glossary-epic-1964).

| Old | New |
| --- | --- |
| `GoalID` / `GoalDetector` / `GoalConcept` / `Domain` | Concern / Inspection / Type / Department |
| `RoutineReview` (`Routine*`, done #1979) | Rounds (`Rounds*`) |
| `Goal` row, `Epoch` | Standard, Episode |
| `Response` concept + `Incident` row | Incident (type and row); "Response" is prose |
| `Rule` | Safeguard |
| `NeedState` | Finding (Met / Unmet / Unclear), Situation (Active / Clear / Unclear) |
| `GoalMethod`, `goal_methods` | Method, `methods` |
