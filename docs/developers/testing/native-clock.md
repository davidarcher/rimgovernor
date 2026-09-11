# Verify native clocks and interruptions

[Documentation](../../README.md)

Use disposable native sessions to verify player holds, exact tick windows and autosave
boundaries.

[Native test prerequisites](README.md#native-test-prerequisites).

## Canonical owned clock

Run `scripts/native_typed_clock_acceptance.py --root /worker/run` through
`scripts/container_scenario.py` with a fresh production native package and profile.
Use `--rendered` with the launcher's `--display xvfb` for graphical acceptance.
The scenario requires no fixture tools. It advances a bounded tick budget through
`advance_game`, checks renew/speed/pause ownership, exact receipt replay and
cross-family attempt conflicts, then verifies authority revocation interrupts play.
Raw ProtoJSON exchanges and immutable event history are retained under
`native-typed-clock-acceptance/`; inspect `result.json` as well as launcher cleanup.

`contracts/tests/native-clock` exercises the production typed runtime against
controlled game/SDK seams. These checks cover fault cases without establishing
actual Harmony hook behavior or native simulation acceptance.

`scripts/native_player_input_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` runs a visible isolated game. After each `ready.json` update, send
the requested Space or number-row 2 key through the actual window input path. The probe
requires native external-pause/speed attribution, a persistent Manual hold, rejection of
a stale controller write, unchanged paused pawn state, and successful explicit resume to
an exact tick boundary. API time-speed calls are used only for setup and explicit
resume, never as the input under test.

## Letter ordering and fixture incidents

For deterministic letter ordering, build `scripts/fixtures/InterruptionFixtures.csproj`
and, with every game stopped, temporarily place its output DLL under the installed
observation mod's `BridgeTools/InterruptionFixtures/` directory. This separate test
assembly calls the real `LetterStack.ReceiveLetter`; it does not change production code,
pawns or saves. Run `scripts/native_interruption_acceptance.py` with the same
source-root/output arguments. It checks exact letter ID attribution, same-frame
non-letter pause/speed precedence, non-pausing threat preemption, nonstopping
announcement delivery, and stale dispatch rejection after native load. The profile must
use the ordinary `MajorThreat` automatic-pause preference. Remove the test DLL after all
owned games stop, and preserve its hash in evidence. For explicit join-scenario setup,
the same test assembly provides `test/join_incident`: preview eligibility with
`dryRun=true`, then use `dryRun=false` to request the ordinary native WandererJoin
event. Record returned before/after IDs, actual joined-pawn work settings and outcomes
separately from the incident receipt. This setup tool is unavailable to model execution.

## Injury preemption

For actual injury preemption, run `scripts/native_combat_smoke.py --require-interruption
--source-root <prepared-root> --output <fresh-directory>`. An ordinary attack on
existing wildlife must cause a native colonist health stop, deliver controller evidence
and reject the old attack revision. Target injury alone cannot pass this variant. The
disposable test may acknowledge one observed Ancient danger warning; production does not
automatically acknowledge it. The isolated game is stopped in cleanup and the report
records termination.

The combat probe also accepts `--staged-root /worker/run` after `container_worker`
has staged a fresh private Linux game. It discovers GABS from that root's config.
Mount the task source and its Git metadata read-only with Linux `GIT_DIR`,
`GIT_COMMON_DIR` and `GIT_WORK_TREE` paths for source provenance. Do not combine
the staged-root option with source/output or patient-save. A native injury-stop
probe does not establish autonomous tactics or low-health rearming acceptance.

`test_medical_triage.py`, `test_medical_recovery.py` and
`test_execution_windows.py` cover patient urgency, native preview fallback,
non-preemption of existing tending, direction-bound recovery and repeated
low-health combat clock refusal. These are controller fixtures; actual medical
outcomes and external pawn-order interruption remain native acceptance work.

## Exact tick budgets

Run `scripts/native_tick_budget_acceptance.py --source-root <prepared-root> --output
<new-directory>` with `controller` on `PYTHONPATH`. Each speed/budget case reloads the
unchanged baseline. The native companion must independently pause at 1, 37 and 600 ticks
at Normal, Fast and Superfast, retain its deadline across renewal, and remain paused
afterward. The probe also checks external native speed/pause commands, lease expiry
before the deadline and retirement on load changes. Add `--rendered` for a visible game.
Native clock commands reproduce time-state transitions; they do not certify physical
keyboard input, autosaves or every danger race. Keep all failed reports.

## Autosave and mixed pending work

`scripts/native_autosave_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` tests an ordinary one-day autosave during a 61,000-tick Superfast
window. Install both the current observation bridge and headless build with every game
stopped; `build_native_mod.ps1` accepts an optional `-DotNet` path. The isolated profile
enables the ordinary pause-on-load preference. The test requires long-event/clear
events, an unchanged exact tick deadline, a newly written native save whose saved tick
matches the event, and a paused reload with the same colony and a new load token. It
records observation, identity and headless DLL hashes and does not edit save XML or pawn
state. Other autosave intervals need an appropriate `--ticks` value. Restore the
original installed DLLs after all tests stop. Add `--mixed` to retain one issued
construction placement, its unissued material reservations, a pending growing zone and a
pending work setting through the save boundary and reload. `--compact-construction` uses
two wall placements on scarce-stock seeds. Ordinary starting supply pods receive 600
ticks to land before setup. The probe requires unchanged controller receipts, progress,
reservations and native action count, then rejects a stale write after reload. Run
`scripts/mixed_autosave_audit.py --reports <first-result.json> <second-result.json>
--output <audit.json>` to require two distinct colonies, immutable boundary evidence,
unchanged deadlines, exact tick completion and verified process cleanup. Physical
keyboard input is accepted separately.

## Related reading

[Testing](README.md) · [Backlog](../../BACKLOG.md)
