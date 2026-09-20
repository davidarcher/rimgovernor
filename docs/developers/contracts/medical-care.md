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
operations. An absent tracked patient cannot certify recovery. Per-condition Go Facts preserve severity, immunity, tended state and tend quality,
plus instantaneous severity/day and immunity/day (60000 game ticks). Severity/day
includes native immunizable and tending modifiers; immunity/day uses the native
immunity record. Missing fields remain unknown, including rates for conditions
without an immunizable component or a missing immunity record. These observations
are current facts, not disease-prognosis estimates. Stable chronic monitoring does
not time out completed work settings; pending work still has the normal watchdog.

Confirmed interrupted tending may resume within the existing recovery bound. An
unavailable doctor can be replaced through a newly validated shared plan step;
the original action and receipt remain retained. The current load, native tick,
player direction and native ordered-job generations must match. Unknown write
results and changed native player orders do not authorize retries.

## Go planning

The Go routine reviewer keeps `CriticalMedicine` a priority-1 emergency, suspending
every other goal, only while a critical patient is bleeding or downed with a tend
outstanding, or that count is unknown (`policy.UrgentPatients`). A living colonist
who merely needs tending, a chronic condition among them, or who is downed with
nothing to tend (malnutrition, exhaustion, a tended wound) keeps the goal active
at priority 2: the same tend and rescue methods serve them, but the colony's other
work and its clock go on around it rather than parking behind a condition only a
bed and ticks can clear (#66, #304). The executor's emergency gate
(`policy.EvaluateEmergency`) holds dispatch as `critical_medical` on the same
terms: a bleeding or downed-untended colonist, or an unknown health fact, holds
every action; a colonist who only needs tending is the tend planner's patient,
one downed with nothing to tend is the rescue planner's, and neither holds
anything. The clock window acknowledges every colonist known downed so the
native watcher lets their rescue and recovery take ticks (#213).
The reviewer also projects ongoing care into a separate priority-2
maintained need. Its same-tick native census distinguishes chronic conditions from
urgent tending. Tracked patient identities persist through Manual and restart;
missing or dead patients and incomplete condition lists cannot certify recovery.
World replacement and tick rewind clear that history. The shared goal retains
cancellation and renewed-deficit semantics. Go care orders, detailed clinical
evidence and monitoring composition remain in G01.07a/e.

## Medicine selection

The medical routine chooses an autonomous care ceiling from usable stock and
fresh disease facts. Early flu uses herbal medicine. A projected loss or tie in
the immunity race, native life-threatening illness, plague, or malaria at severity
0.5 or above prefers industrial medicine. Herbal is the fallback when industrial
is unavailable; industrial is the fallback when no herbal remains. With neither,
tending continues without medicine. Glitterworld is never selected automatically.
Unknown clinical or stock facts defer a change.

Auto reassesses the current care setting, including settings changed during Manual.
Care writes use the existing typed `PatchPawn.medical_care` operation through Hands,
with the current settings token and observed care readback. A changed token refuses
a stale action; the next review plans from the new facts. Care-only writes also
permit downed patients, without enabling work or timetable writes to them.

While the medicine reserve is low, it contributes the configured herbal target per colonist to
`MaintainResource`; explicit higher resource floors remain authoritative. The
medical reserve's existing bill and wild-healroot methods continue to work.

## Disease care

When a disease's projected immunity lead over lethal severity is less than a
day, the medical routine enables PatientBedRest and retains rest until the
tracked condition reaches full immunity or leaves a complete census. Native tending, bed selection,
severity progression and immunity gain remain ordinary RimWorld simulation;
medicine selection follows the tier rules above. Missing patients or health
observations cannot establish recovery, and death is a failure.

`medical/disease` starts from the Core-only tribal8 baseline with Plague on two
colonists, both untended, and exactly five herbal plus five industrial medicine.
The disposable `test/medical_plague_prepare` survival variant supplies medical
sleeping spots and disables bed rest initially. The case observes Auto selecting
`NormalOrWorse` while industrial stock remains, subsequent native tending and
bed rest, and explicit full immunity for both patients with all eight original
colonists alive. Needs are frozen and the storyteller is quiet to isolate the
disease race. Contagion and organ-decay or blood-rot surgery are outside this case.

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
