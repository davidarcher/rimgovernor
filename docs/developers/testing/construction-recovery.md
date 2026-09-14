# Verify construction and supply recovery

[Documentation](../../README.md)

Use these focused probes for construction changes, supply recovery, ordinary consumption
and retained work evidence.

[Native test prerequisites](README.md#native-test-prerequisites).

## Go HTTP building service

Build a private native package with `-Fixture GuardedConstructionFixture`. Place
the checked Go service binary in the input profile as `rimgovernor-go`, then run
`scripts/native_building_service_acceptance.py --root /worker/run --go-binary
/inputs/profile/rimgovernor-go --go-source <source-commit> --go-sha256 <binary-sha256>`
through `scripts/container_scenario.py`. Supply the exact source commit and SHA256
from the retained Go build handoff; the harness validates their format and checks
the copied binary before launch. The input mount path is required: worker profile staging
copies only selected game files. Add `--rendered` with launcher `--display xvfb`
for graphical acceptance.

The emergency service harness uses the same required handoff arguments. Both
launch `--player-control` and use shared `/api/player` control routes. Keep previous
accepted artifacts with their original source and binary records; new trials use
a fresh handoff and output directory.

The scenario retains one database through three fully joined service processes.
It verifies submission/replay without native writes, explicit authority and one
blueprint, Manual disable, ordinary pawn construction through `advance_game`, then
disabled restart reconciliation without reacquisition or redispatch. Complete SDK
event sequences attribute each service phase separately from orchestrator reads
and tick control. No forced GABS takeover is used. Inspect both the inner result
and launcher database-integrity/cleanup evidence; an unjoined process fails the run.

For Go-owned clock operation, use `scripts/native_go_clock_acceptance.py` with
the same launcher and binary handoff arguments. Also compile
`scripts/fixtures/InterruptionFixtures.csproj` against the private game/SDK inputs
and place its DLL under `RimGovernor/BridgeTools/InterruptionFixtures` in the
input mod package. The fixture schedules a real threat letter before handing
exclusive bridge access to Go. The service starts with `--clock-control` and must
stop for that letter, retain an explicit acknowledgement and its replay, pause
on Manual, observe completed pawn construction, join shutdown and restart disabled.
SDK operation history must attribute clock control to Go throughout each service
phase. The harness does not advance the clock on the service's behalf.
The generated colony must permit healthy-colony play. Inspect the retained
prepared-colony observations if native hostiles or medical needs prevent the
first window; use a fresh disposable colony without relaxing safety policy.

`scripts/construction_refinement_acceptance.py --source-root <prepared-root> --output
<fresh-directory> --model <local-model-id>` checks policy refusal before removal,
real-model relocation with dependent shared execution, interrupted removal, player
preservation and paired restart. Failed trials retain their checkpoint.
`scripts/construction_resume_acceptance.py --source-report <failed-result.json> --output
<fresh-directory>` can finish the load checks from that immutable checkpoint after an
infrastructure startup failure. It deliberately queues the old cancellation in the new
load to test its context guard, then requires a fresh local-model request and exact
native removal delta. Neither probe certifies pawn construction or cooling.

## Affordability and supply recovery

`scripts/construction_recovery_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` uses ordinary forbid/unforbid designators to make a costed two-wall
commitment temporarily unaffordable. It requires an exact pre-write resource failure,
refusal while stock remains forbidden, same-action recovery after native availability
returns, and exactly two observed native blueprint identities. The probe runs shared
Hands and preserves the recovery history with no inference; blueprint issuance does not
certify subsequent pawn construction. `scripts/supply_recovery_acceptance.py
--source-root <prepared-root> --output <fresh-directory>` makes a starter-stock allow
preview obsolete through an ordinary allow command. It requires a known pre-write
refusal, fresh native absence counts, same-action completion and retained recovery
history with zero Hands writes. Buffered player pause/speed interruption guards are
covered by controller tests.

## Material consumption and recovery fixtures

`scripts/resource_consumption_acceptance.py --checkpoint <native-resource-checkpoint>
--output <fresh-directory>` copies an unchanged native save into a separate profile. It
admits an ordinary bill job, reads unfinished work from ordinary native saves, then
forbids a remote ingredient stack while verifying the same job remains active.
Completion must stop with nonpositive work remaining, unchanged unfinished ingredients
and no product. Ordinary unforbid must permit exactly one output while the reserve
prevents another cycle. Bill settings, filters and suspension remain unchanged. The
manifest records the actual imported controller source and separate harness hash; no
policy setter interrupts the job during the stock-change test. Use `--material-identity`
to verify that ordinary replacement of a separately grounded Steel wall blueprint
produces distinct Wood blueprint identities and correct native material readbacks.
Different materials must not return a false `already_present` receipt. Native
replacement is allowed; this is not a placement refusal or construction-completion test.

Add `--recovery-fixture` to a lifecycle campaign to retain the native resource recovery
history through subsequent ordinary simulation. Measurements include immutable archive
hashes, live or archived recovery histories, native action counts and the native clock
state at autosave boundaries. The lifecycle audit rejects changed or missing prior
archive records and recovery history. Joined-pawn work acceptance compares completed
deterministic assignment deltas with later native work-table readbacks for every joined
pawn. These readbacks verify applied settings; actual bed use separately requires native
`inBed` and `bedThingId` observations for every starting and joined colonist.

## Bed use

`scripts/native_bed_use_acceptance.py --source-root <ordinary-prepared-root> --output
<fresh-directory>` verifies additional-seed actual bed use. Routine construction must
supply indoor capacity for the entire starting roster. Once native threats, tending
needs and active mental states are clear, the fixture enters Manual and applies an
ordinary sleep timetable. Every starter must then have a native bed identity and `inBed`
observation. This bounded fixture certifies bed use, not sustained survival or emergency
handling.

## Lifecycle and hunting archives

Add `--archive-fixture` to start a lifecycle campaign with an ordinary completed
work-setting action and method in the durable archive. Native work readback must verify
the action before explicit retirement. The audit requires nonempty initial archive
hashes and their unchanged preservation throughout the campaign.
`scripts/hunting_archive_acceptance.py --source-root <ordinary-prepared-root> --output
<fresh-directory>` uses screened wildlife and shared Hands to verify an actual Hunt
designation. Explicit retirement must preserve the exact action, method and
target/signature in three nonempty archive tables through at least 6,000 native ticks.
This verifies designation evidence retention, not killed prey or completed pawn labor.

## Related reading

[Testing](README.md) · [Issues](https://github.com/davidarcher/rimgovernor/issues)
