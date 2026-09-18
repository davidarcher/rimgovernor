# Medical care contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`CriticalMedical` selects native-approved doctor/patient pairs by bleeding deadline,
native life-threatening state and stable identity. It preserves `NoCare`, player
work overrides and self-tend policy. Completed treatments permit a later tend in the
same goal episode. A later confirmed, completed autonomous treatment can admit a
new step after fresh native eligibility; pending and uncertain work cannot. Prior
actions and receipts remain retained across persistence and method archival.
The current doctor's exact job target distinguishes treatment from tending somebody
else. A receipt does not establish completed treatment.

If triage has no eligible pair after combat clears, `CriticalMedical` can release
current-load controller-owned drafts through Hands before selecting treatment on
a fresh read. Player drafts and work overrides remain protected. Each release
rechecks paused native threat counts and exact incapacitated identities; an active
or unknown threat retains the squad. Existing tending is never interrupted for
cleanup. Changed patient/worker evidence can reopen an unavailable-pair hold.

`MaintainMedicalCare` retains native visible condition identities, severity, immunity,
retend timing, care policy and medical-rest state. It enables ordinary Patient and
PatientBedRest work when available and not disabled by a player override. Native jobs choose
beds. Missing observations, unavailable work or player restrictions produce explicit
blockers. Chronic conditions remain visible without automatically choosing elective
operations. An absent tracked patient cannot certify recovery. These observations
are current facts, not disease-prognosis estimates. Stable chronic monitoring does
not time out completed work settings; pending work still has the normal watchdog.

Confirmed interrupted tending may resume within the existing recovery bound. An
unavailable doctor can be replaced through a newly validated shared plan step;
the original action and receipt remain retained. The current load, native tick,
player direction and native ordered-job generations must match. Unknown write
results and changed native player orders do not authorize retries.

## Go planning

The Go routine reviewer keeps `CriticalMedicine` a priority-1 emergency, suspending
every other goal, only while a critical patient is downed or bleeding or that count
is unknown (`policy.UrgentPatients`). A living colonist who merely needs tending, a
chronic condition among them, keeps the goal active at priority 2: the same tend
method treats them, but the colony's other work and its clock go on around it
rather than parking behind a condition nobody can clear. The executor's
emergency gate (`policy.EvaluateEmergency`) holds dispatch as `critical_medical`
on the same terms: a downed or bleeding colonist, or an unknown health fact,
holds every action; a colonist who only needs tending is the tend planner's
patient and holds nothing (#66).
The reviewer also projects ongoing care into a separate priority-2
maintained need. Its same-tick native census distinguishes chronic conditions from
urgent tending. Tracked patient identities persist through Manual and restart;
missing or dead patients and incomplete condition lists cannot certify recovery.
World replacement and tick rewind clear that history. The shared goal retains
cancellation and renewed-deficit semantics. Go care orders, detailed clinical
evidence and monitoring composition remain in G01.07a/e.

## Surgery

`home/medical_operations` discovers current patient recipes, body-part indices,
native ingredient definitions and counts, practitioner skill requirements, and
current operation bills. Its catalog is patient-specific. Available ingredients
and doctors do not certify bed access, sufficient reachable medicine or eventual
success; normal native work selection still checks those conditions.

`RequestSurgery` requires an explicit player choice of the inspected patient,
recipe and part. It creates a `PLAYER` action executed by Hands. Runtime dispatch
requires its recorded semantic intent, matching arguments, current direction/load
and unchanged health identity and care policy. No autonomous elective-surgery method
exists. The native operation rechecks eligibility in its main-thread transaction
and queues an ordinary medical bill. Existing patient bills are preserved and prevent
adding competing work. Patient policies, beds and native medicine restrictions remain
in force. Recipes requiring extra dialogs, faction violations or unsupported health
effects remain inspection-only.

Completion requires the expected newly added condition on the exact body part or
observed removal of the targeted condition. Bill removal, delivery receipts and elapsed
ticks are insufficient. Failure, cancellation, suspension, death and missing health
remain explicit; failed operations are never automatically repeated. Surgical health
changes and postoperative recovery are separate outcomes.

The native setup used by medical acceptance
is excluded from production builds and from the model execution surface.

The supervised clock accepts short-lived surgical recovery IDs only from confirmed,
current-direction patient operations in the current colony/load/map. Native sweeps
permit downing only while the identified patient is alive, anesthetized, in a bed,
not bleeding, not dangerously ill and above half health. Injury and death guards
remain active. The allowance expires at the configured work window; it is not a
general exemption for downed patients. Changed context invalidates surgical
completion tracking, and stalled operations expose a bounded no-progress failure.

Stable downed patients may share survival priority while ordinary caregivers work.
Native `stableRestEligible` requires a living, undrafted colonist in bed, above half
health, without bleeding, a current tending need, a mental state, anesthesia or a
life-threatening condition. The controller requests monitoring only from current
health observations and an active medical-care goal. Native `medicalRestIds` windows
are limited to 600 ticks and recheck each patient; changed eligibility stops the
window for another review. Injury and death guards remain active. This permits
feeding and rest without certifying recovery or satisfying the medical stability gate.
