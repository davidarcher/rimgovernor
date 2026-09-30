# Working agreement

`AGENTS.md` is the source of these instructions; `CLAUDE.md` is a symlink to
it. Machine-level setup (game copy, mod build, running harnesses) is the
[agent runbook](docs/developers/agent-runbook.md); which checks a change
needs is [choose-tests](docs/developers/testing/choose-tests.md).

## The loop

Take the shortest valid path. Run only the required checks, stop exploring
once acceptance passes, and land immediately.

1. Work on a task branch in your own worktree; `git merge main` once at
   session start.
2. Edit; `go run ./cmd/test` from `go/` is the test loop. It tests the
   packages the working tree changed and their in-module importers
   (`./...` only when `go.mod`/`go.sum` changed) and names the acceptance
   harnesses the change touches. Do not follow it with `go test ./...`.
3. Commit each completed iteration. Checkpoint commits are authorized; do
   not ask. Size an iteration to a coherent milestone, not the smallest
   possible edit, so slow checks run once against meaningful progress.
4. Landing needs no acceptance run. Affected areas are proven by `go test`
   and the on-demand full tier (#752); the scheduled nightly runs the
   thirteen end-to-end cases (`-tier nightly`, a signal, not a gate). Run a
   tier (`acceptance suite -tier smoke` or `-tier land`) only when you want
   the change proven before it lands, hand its output to `cmd/land
   -results`, and name the run in the commit message; never run the areas
   with `acceptance run` first and then a tier, which runs every case
   twice. Add `-resume` to carry the checkpoint
   rings your failed `acceptance run`s left in `-root`: resumed rows pass,
   are listed under `resumed` and named in the landing, but prove the fix
   past the resume point only, so a change to early behaviour runs fresh.
5. `go run ./cmd/land [-results <suite output>]` from the branch worktree,
   never piped through `tail` (nothing prints until it ends). The lane
   titles the squash with the branch tip's commit subject, so make the
   milestone commit the tip and fold fixups into it first.
   The lane takes the repository lock, merges `main` into the branch,
   refuses a presented suite that failed (resumed rows are recorded),
   squash-lands on the
   `main` checkout, resets the branch to `main` and closes the branch's
   GitHub issue with the landing commit. Call it once and move on; land
   each ready milestone rather than holding a branch until the whole task
   is done. Rebase or merge by hand only to resolve a conflict it reports.
   An orchestrator that spawned worktree agents removes each agent's
   worktree once the agent reports (`git worktree remove <path>`, then
   `git branch -D <branch>` when the branch holds nothing off `main`);
   the harness locks a running agent's worktree, so neither the agent
   nor the lane can remove it.
   The issue closes when the landing meets the acceptance written in its
   body, not when every follow-up you can think of is done: comment the
   follow-ups in one line (or file them as issues) and let the lane close
   it. An issue left open with a "remaining:" paragraph reads as blocked to
   every other agent sequencing against it.
6. Continue to the next milestone of an authorized task without waiting to
   be re-prompted. An issue is finished or you are still working it:
   landing a milestone is not a stopping point, and a comment listing
   remaining work you could do yourself is not a reason to stop. Stop only
   on a blocker you cannot clear, and name it (a commit, an issue number,
   a decision needed). Readiness checks and assessments that change
   nothing are not work; do not post them.
   An issue whose acceptance is an acceptance case is finished once the
   case is written, registered and builds clean: it runs in the next
   full tier, and a failure there opens a new issue. Do not hold the issue
   open to run the case yourself.

`main` moves constantly and that is never a reason to redo anything: a test
or harness that passed on the branch's code stays passed, the lane's merge
does not invalidate it, and a second rerun-and-land cycle for one milestone
is forbidden. If something is left unverified, say what in the commit
body; do not open an issue for it. The next
full-suite pass (#363, acceptance on CI) verifies every unverified landing
at once; per-landing issues only pile up until then.

## Simplify before you extend

Before adding a layer (flag, fallback, retry, cache, special case, wrapper,
parallel path), check whether the problem comes from an earlier layer that
should not exist; if so, say so and propose removing it instead. Cite the
issue text or user message that asks for any new behaviour; if you cannot,
it is an assumption: flag it, do not build on it silently. When a second
fix for the same symptom would add another guard, stop and explain why the
first fix failed. Every landing report ends with one line:
`Complexity: added X / removed Y / deletion candidate: Z` (or `none`).

## Never

- Open a pull request, or push to GitHub. GitHub holds issues only; the
  maintainer pushes `main` by hand.
- `git reset --soft main` to squash, or edit the `main` checkout directly,
  not even to try a fix on the user's launcher game (which builds from
  `main`): land it, then restart the launcher (#965).
- Kill `RimWorldWin64.exe` by image name; peers' games run
  beside yours. Stop your own by root or pid (runbook).
- Replace a DLL under a game install some RimWorld is running from. Only a
  game started from that same install counts: your worktree's mod lives in
  its private `.rimgovernor/native-rimworld/` copy, so peers' games and the
  user's launcher game (from `main`) never block `acceptance setup
  -rebuild` in your worktree; stop your own game first (`acceptance stop
  -root <root>`). The Steam install and the shared
  `.rimgovernor/isolated-rimworld` copy are never yours to replace (runbook).
- `go clean -cache`, or set a private `GOCACHE`.
- Rebuild the controller binary or the mod while a harness is running from
  them.
- Commit generated builds, logs, saves, databases or temporary scripts.
- Add Python to the repository. Tooling, acceptance cases and analysis
  are Go (`go/cmd`, `go/internal/nativeaccept/cases/<area>`); throwaway
  Python stays in the scratchpad.

## Tool pitfalls (Windows harness)

Each of these fails the same way in every session; none is a judgment call.

- Build the mod through `acceptance setup` (`-rebuild`, `-fixture A,B`),
  never `scripts/build_native_mod.ps1` by hand: the
  harness refuses any PowerShell command carrying a `C:\Program Files`
  argument, and the script needs three Steam paths `setup` discovers itself.
- Never `sleep N && <check>` to wait on a run; the harness blocks it. Launch
  long commands with `run_in_background: true` and wait for the notification,
  or use `Monitor` with an until-loop.
- Write files with the Write tool, not `cat <<'EOF'` in Bash: the Bash tool
  re-escapes heredoc bodies, so any apostrophe or backslash in the content
  breaks the whole command (`unexpected EOF while looking for matching`).
  CRLF files (docs, AGENTS.md) need newline-preserving edits.
- `python`, not `python3`; `python3` is the Microsoft Store stub.
- Read GitHub issues with `go run ./cmd/issue <n>` (from `go/`): body and
  comments in one call, the full text also written to `issue-<n>.md`; `-last k`
  for the newest comments. Not `gh issue view --comments`: redirected, it
  prints only the comments and nothing on an uncommented issue. Never
  WebFetch a github.com URL.

## Issues

Open a GitHub issue (`gh issue create`) for anything you would otherwise
leave as "follow-up" or ask about in a summary: bugs found in passing,
deferred scope, decisions needed. Also for what you notice while developing
and are not fixing: a slow test or check, a workflow step that makes no
sense, a performance problem, an architecture problem or smell (a layer in
the wrong place, duplicated logic, an interface that fights its callers).
Do not swallow these; the issue is how they get scheduled. Not for an
unverified landing (see step 5 above). One issue per
item, terse title, concrete evidence (file, commit, log line), what would
resolve it, labeled `priority:P0`/`P1`/`P2` or `area:G01`/`N01`/`tooling`.
Check open issues first and comment on a match instead of duplicating.
Cite the number in your report instead of restating it. Status goes in
issue comments, not chat: a sentence on what landed or remains and the
commit, not a file-by-file narrative.

## Checks

- Whole-module race checks and repeated package-wide race stress runs belong
  in nightly validation, not the local landing loop. Do not run them locally
  unless explicitly requested. For a concurrency fix, use a focused
  `go test -race -run <tests>` check when useful; repeat only those tests to
  reproduce a flake. Prefer synchronized events or virtual time over
  scheduling-sensitive wall-clock bounds. The nightly `remote-acceptance`
  workflow runs the whole module with `-race` on Linux.
- Test deadlines are hang guards, not latency assertions. A test that fails
  on `context deadline exceeded` or a `checktesttimes` breach under load
  (`./...`, `-race`, a busy CI runner) has measured the machine, not the
  code: widen the guard to the accepted maximum (bridge `testBudget`,
  `playerFixture` CallTimeout, the 60 s `checktesttimes` gate) or assert
  the event instead of the wall clock. Never tighten a deadline per test,
  shave a test to fit a budget, or open a per-test timing issue for it
  (#546, #551-#557 were all this, closed by one landing).
- Pyramid: many fast Go unit tests (via `cmd/test`), fewer integration
  tests, a small set of targeted native acceptance cases run by
  `go/internal/nativeaccept/cmd/acceptance` (`acceptance run
  <area>/<case>`, `acceptance suite`; `cmd/test` names the areas a change
  owes) against a real headless RimWorld.
  Before a slow check, say what changed behaviour it verifies and why the
  cheaper check is insufficient.
- `task build && task test` runs every project's gates (dashboard,
  protobuf, C#); use it when the change touches those, not for Go-only
  work.
- A receipt does not prove pawn work completed: assert the native
  postcondition. Distinguish compilation/protocol checks from gameplay
  validation.
- After an acceptance failure, add a fast regression test where feasible
  and rerun that case. The rerun resumes from the run's last checkpoint
  by default (`resuming <case> from t+7m ...` on its first line; #249);
  `-fresh` starts over; the land suite runs fresh unless `-resume`. An
  edit to a case's reads or asserts alone reruns with `-postmortem-only`
  (#275), which reloads the failed bundle and runs only its `Postmortem`
  phase; an edit to the code a case's late stage exercises iterates with
  `acceptance dev <case>` (#274), which rebuilds `rimgovernor` and reruns
  `Run` and `Postmortem` from a bundle on the kept process each time.
- Native performance work (a snapshot family's cost) iterates with
  `acceptance profile-capture -root <dir>` (#1320), not a sustained run:
  it heals a stale native mod, reloads the newest `sustained/colony`
  checkpoint (its own root first, then the main checkout's and peer
  worktrees'; `-from`, `-case`, `-save`) on the kept process, times
  `SnapshotFrames.Capture` `-n` times paused (`test/profile_capture`) and
  prints p50/p90/max and rows per family and `ObservationWork.Detail`
  span; `-equality` adds the ColonyFacts optimizations off/on check.
- A new case starts from a fixture that already exercises the behaviour
  (a committed save, a `test/*_prepare` op, or a programmatic start) and
  follows the performance checklist in choose-tests: Core-only, quiet
  storyteller, stall-bounded waits, minute-scale budgets. Playing a colony
  into its precondition for 20 minutes is a fixture bug.
- A new fixture case starts on `cases.LabStart()` (the blank 100x100 lab,
  #729) and spawns what it needs at known offsets from the centre the op
  replies; it never searches a random world for a site.
  `TestFixtureCasesStartPinned` refuses an unpinned random start unless
  the case is on its exemption list with a terrain reason.
- Snapshot first (#738). A new native case states, in its scope text, why
  a Go snapshot test over recorded colony facts cannot cover it (a native
  op or read contract, an end-to-end signal, vanilla physics). A planner
  decision is a snapshot test, not a case.
- A failing planner-decision case (bucket A in #738) is replaced by a
  snapshot test and deregistered, not fixed: do not repair its fixture,
  budget or staging.
- Checks named in old commits or issues may no longer exist; trust
  `go/internal/nativeaccept/cmd/` over history.

## Architecture

Start with the [documentation map](docs/README.md), the
[system overview](docs/developers/architecture/overview.md) and the
[development process](docs/developers/development-process.md); read the
component guide and contracts for the subsystem you change. Runtime: Go
(`go/`), React (`dashboard/`), RimBridgeServer (over GABP) and
`integrations/rimgovernor-native`; native acceptance tooling is Go.

Non-negotiables: RimWorld owns simulation and normal game rules hold
(discover native schemas; editor/cheat operations stay outside model
execution). One shared goal/action system with deterministic Hands;
advisers never write game orders or own colony invariants. Local LM Studio
models only, no silent paid-provider fallback. Typed contracts at
boundaries; explicit component ownership; integrate through the existing
architecture. Manual control semantics are in the
[control loop guide](docs/developers/architecture/control-loop.md#manual-control).

## Docs and comments

Concise and forward-looking: current behaviour, contracts, constraints,
useful rationale. No design archaeology, implementation diaries or
accounts of superseded approaches, in prose or in commit messages beyond
the evidence. Organise for players and developers; link rather than
repeat; keep the [documentation map](docs/README.md) current and update
architecture and procedure docs when behaviour changes.
