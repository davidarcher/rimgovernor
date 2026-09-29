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

If triage has no eligible pair after combat clears, the squad's drafts free
up through the undraft sweep once the fight closes and no live plan needs
them (#939); `CriticalMedical` then selects treatment on a fresh read. An
active or unknown threat keeps the fight open and the squad drafted.
Existing tending is never interrupted. Changed patient/worker evidence can reopen an unavailable-pair hold.

`MaintainMedicalReserves` (care phase) retains native visible condition identities, severity, immunity,
retend timing, care policy and medical-rest state. It enables ordinary Patient and
PatientBedRest work when available and not disabled. Native jobs choose
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
Care writes send `WorkSettingsIntent.medical_care` on Actions/Apply through Hands;
native checks the pawn when it applies and a ceiling already set applies again. A
refused write is replanned from fresh facts on the next review. Care-only writes also
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

The Plague tier decision (two Plague patients, industrial medicine in stock:
both and only they go to NormalOrWorse) is a colony snapshot test,
`TestSnapshotDiseaseSelectsIndustrialCare` (#750).

## Surgery

Surgery facts ride on the per-pawn care read (`PawnHealth.missing_parts` and
`PawnHealth.operations`, #1161); `ReadMedicalCatalog` is retired. Native
discovers recipes through `RecipeWorker.GetPartsToApplyOn`/`AvailableOnNow` and
computes `success_chance` with the recipe's own `SurgeryOutcomeEffectDef` comps
for the best eligible doctor, substituting the best colony medical bed when the
patient is not in bed and the best permitted medicine on the map. It also
reports the eligible doctor count, whether ingredients and medicine are on the
map, vanilla `IsViolationOnPawn`, and `lethal` (`WouldDieAfterAddingHediff`, or an
execution). Go maps them into `policy.CarePawn` and does no surgery math.

`SurgeryIntent` on Actions/Apply (#1162) queues one operation bill; see the
`surgery` row of [action contracts](action-contracts.md). No planner sends it
yet (epic #1160).

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
