# Verify room refinements and cancellation

[Documentation](../README.md)

Verify native room adoption, thermal changes and exact construction removal through
shared player intent.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimgovernor/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

`scripts/freezer_expansion_acceptance.py --source-root <prepared-root> --output
<fresh-directory> --model <local-model-id>` builds two separate powered cold-storage
rooms with ordinary harvested materials, insulation and pawn labor. Actual local-model
requests set cooler temperatures, reserve steel and expand storage while preserving the
first freezer. Completion requires both roofed rooms below freezing with their native
stockpile cells, including three stable native windows after expansion. Use
`--resume-report <result.json>` instead of `--source-root` to resume an immutable
powered, supplied or working-freezer checkpoint after an infrastructure failure.

## Thermal adoption and resume

`scripts/adopted_room_thermal_acceptance.py --freezer-report <passed-result.json>
--output <fresh-directory> --variant hot|cold` then adopts an edited room, preserves
existing furniture, fills native sleeping capacity and verifies ordinary fueled thermal
furniture and temperature recovery. Each variant resumes the immutable checkpoint
independently, verifies exact native food filters and storage priority, and records both
freezers across three loaded native windows before editing. These probes require the
named local model to be loaded; retain failed reports and distinguish powered
construction from actual cooling. For an integrated controller source check, run
`scripts/adopted_room_resume_acceptance.py --thermal-report <passed-hot-or-cold-result>
--output <fresh-directory>`. It reloads the completed paired checkpoint, verifies native
room and fueled furniture state, refreshes adoption with the local model, performs one
legal furniture edit through current spatial admission and Hands, and verifies that
invalid adoption geometry preserves the current plan.

## Exact construction cancellation

`scripts/cancel_construction_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` tests the native exact-target cancellation contract in a private
paused colony. It creates ordinary blueprint orders and a zero-work sleeping spot, then
verifies dry-run preservation, stale colony/map/load and metadata refusal,
completed-building refusal, exact blueprint removal and repeated-request refusal without
retargeting its neighbor. Add `--shared` to create a room through the semantic command
and shared Hands, prevalidate its cancellation, drop a successful native removal
receipt, and verify that a fresh request removes only the remaining targets. Repetition
after completion creates no native removals; unrelated pending orders and completed
buildings remain. Add `--frame` with `--shared` to assign ordinary builders and observe
a partly built wooden bed before cancellation. The frame's observed held materials must
return to spawned stock while the native tick remains unchanged. The fixture rejects
footprints overlapping existing orders. Its explicit simulation driver waits for
controller review pauses; an ancient-danger warning can be acknowledged only after
recording it and verifying no active hostile or hunting-predator count. Other danger
stops fail the probe. This does not establish mixed restart acceptance. Add `--chat
--model <local-model-id>` with `--shared` to send the fresh cancellation request through
actual player chat after the lost receipt. The probe checks the resulting exact target
set, native removal, preserved unrelated orders and paused Manual mode. This single
request is not a general model reliability measurement. Install its companion and
restore the previous DLL only with every game stopped.

## Chat refinement and lost receipts

`scripts/construction_refinement_acceptance.py --rooms --wording conversational`
holds the fixture executor while chat admits a room and refines its entrance, then
requires exact native orders for that shell. It verifies relocation, a deliberately
lost removal receipt, conflicting preservation requests across multiple intents,
paired restart and fresh removal of only the remaining pending orders. Use
`--wording explicit` for the alternate request context. These paused checks certify
orders and settings, not pawn-built rooms or general model reliability.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
