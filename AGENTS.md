# Working agreement

`AGENTS.md` is the source of these instructions; `CLAUDE.md` is a symlink to
it. Machine-level setup (game copy, mod build, running harnesses) is the
[agent runbook](docs/developers/agent-runbook.md); which checks a change
needs is [choose-tests](docs/developers/testing/choose-tests.md).

## The loop

1. Work on a task branch in your own worktree; `git merge main` once at
   session start.
2. Edit; `go run ./cmd/test` from `go/` is the test loop. It tests the
   packages the working tree changed and their in-module importers
   (`./...` only when `go.mod`/`go.sum` changed) and names the acceptance
   harnesses the change touches. Do not follow it with `go test ./...`.
3. Commit each completed iteration. Checkpoint commits are authorized; do
   not ask. Size an iteration to a coherent milestone, not the smallest
   possible edit, so slow checks run once against meaningful progress.
4. At the milestone, if `cmd/test` named affected case areas, run the one
   command it prints, `acceptance suite -tier land`, and hand that output
   to `cmd/land -results`. The tier already covers the affected areas plus
   the smoke set, fresh: do not run the areas with `acceptance run` first
   and then the tier, which runs every case twice. (Native behaviour
   changes need this before completion; documentation needs none.) Name
   the run in the commit message. Add `-resume` to carry the checkpoint
   rings your failed `acceptance run`s left in `-root`: resumed rows pass,
   are listed under `resumed` and named in the landing, but prove the fix
   past the resume point only, so a change to early behaviour runs fresh.
5. `go run ./cmd/land [-results <suite output>]` from the branch worktree.
   The lane takes the repository lock, merges `main` into the branch,
   refuses a presented suite that failed (resumed rows are recorded),
   refuses a diff under the native sources or `buildingruntime` without
   one (`-unverified` lands it; name what went unverified in the commit
   body, not an issue), squash-lands on the
   `main` checkout, resets the branch to `main` and closes the branch's
   GitHub issue with the landing commit. Call it once and move on; land
   each ready milestone rather than holding a branch until the whole task
   is done. Rebase or merge by hand only to resolve a conflict it reports.
6. Continue to the next milestone of an authorized task without waiting to
   be re-prompted.

`main` moves constantly and that is never a reason to redo anything: a test
or harness that passed on the branch's code stays passed, the lane's merge
does not invalidate it, and a second rerun-and-land cycle for one milestone
is forbidden. If something is left unverified, land with `-unverified` and
say what in the commit body; do not open an issue for it. The next
full-suite pass (#363, acceptance on CI) verifies every unverified landing
at once; per-landing issues only pile up until then.

## Never

- Open a pull request, or push to GitHub. GitHub holds issues only; the
  maintainer pushes `main` by hand.
- `git reset --soft main` to squash, or edit the `main` checkout directly.
- Kill `RimWorldWin64.exe` or `gabs.exe` by image name; peers' games run
  beside yours. Stop your own by root or pid (runbook).
- Replace an installed DLL while any RimWorld instance is running, yours or
  a peer's.
- `go clean -cache`, or set a private `GOCACHE`.
- Rebuild the controller binary or the mod while a harness is running from
  them.
- Commit generated builds, logs, saves, databases or temporary scripts.

## Tool pitfalls (Windows harness)

Each of these fails the same way in every session; none is a judgment call.

- Build the mod through `acceptance setup` (`-rebuild`, `-fixture A,B`,
  `-production`), never `scripts/build_native_mod.ps1` by hand: the
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
deferred scope, decisions needed. Not for an unverified landing (see step 5
above). One issue per
item, terse title, concrete evidence (file, commit, log line), what would
resolve it, labeled `priority:P0`/`P1`/`P2` or `area:G01`/`N01`/`tooling`.
Check open issues first and comment on a match instead of duplicating.
Cite the number in your report instead of restating it. Status goes in
issue comments, not chat.

## Checks

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
  phase.
- A new case starts from a fixture that already exercises the behaviour
  (a committed save, a `test/*_prepare` op, or a programmatic start) and
  follows the performance checklist in choose-tests: Core-only, quiet
  storyteller, stall-bounded waits, minute-scale budgets. Playing a colony
  into its precondition for 20 minutes is a fixture bug.
- Checks named in old commits or issues may no longer exist; trust
  `go/internal/nativeaccept/cmd/` over history.

## Architecture

Start with the [documentation map](docs/README.md), the
[system overview](docs/developers/architecture/overview.md) and the
[development process](docs/developers/development-process.md); read the
component guide and contracts for the subsystem you change. Runtime: Go
(`go/`), React (`dashboard/`), GABS/RimBridgeServer and
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
