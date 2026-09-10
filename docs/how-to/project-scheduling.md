# Verify durable project scheduling

[Documentation](../README.md)

`scripts/project_postconditions_acceptance.py --source-root <prepared-root>
--output <fresh-directory>` verifies real native zone creation, stockpile priority
and filter edits, crop/sow/cut changes, geometry edits and removal, plus ordinary
zero-work sleeping spots in every facing and wrong-facing replacement. It checks
shared dependency invalidation, explicit restoration, paired restart while held,
receipt preservation and stale-request rejection after native rewind. Zero-work
spots establish native building postconditions, not material-consuming construction.

`scripts/project_scheduling_acceptance.py` takes the same arguments and an optional
`--seconds 480` per-project pawn-work bound. It admits competing costed wall
projects, restricts ordinary stock through native forbidding, interrupts a partial
batch, verifies a paused paired restart, then requires actual pawn construction
and exact material consumption. Restoring stock resumes the retained competitor;
dependent work waits for real completion, and repeated dispatch must issue nothing.
Build its private companion with `-p:ConstructionLedgerFixture=true`. This optional
test-only observer records native Frame completion/failure and before/after resource
counts without changing those methods. The probe reconciles full stock, including
pawn-held items, against those events so ordinary construction failures are counted
as actual losses. Missing/truncated observer evidence fails acceptance. Default
production builds omit the observer.

For local Docker, stage private current companion binaries using the worker
[isolated worker procedure](docker-worker.md), build the task's image, and run each probe as the worker command:
`docker compose -f containers/compose.yaml -p <unique-project> run --rm worker --
python /app/scripts/project_postconditions_acceptance.py --source-root /worker/run
--output /worker/acceptance`. Use fresh worker output for each probe. Staged input
hashes, native saves, paired databases, progress and result files remain there.
No shared installed DLL is replaced.

`shelter_handoff_acceptance.py --restart-pending` disables ordinary construction
assignments for 600 real ticks, drains the native stop event and verifies a paused
paired restart with the same pending shell, identities and receipts. It restores
the player assignments and requires ordinary shell completion and deterministic
indoor furnishing. `resource_policy_acceptance.py --persistence` separately checks
actual bill output/consumption and reserve enforcement across paired restart; its
binary provenance follows the effective game configuration on Windows or Linux.

