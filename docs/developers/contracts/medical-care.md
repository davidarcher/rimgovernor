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
blockers. An absent tracked patient cannot certify recovery. Per-condition Go Facts preserve severity, immunity, tended state and tend quality,
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

## Surgery

`MaintainSurgery` (priority 2, #1164) restores missing or destroyed parts from
the surgery facts on the pawn care read. Per part it takes the best recipe whose
ingredients are on the map (bionic, then prosthetic, then peg) that some eligible
doctor performs with a native failure chance of 20% or less; each patient queues
its most valuable part (capacity weight times part tier) as one medical `ProductionBillIntent` (`patient` set),
and no patient gets a second while a bill is queued or a surgery action is open.
Otherwise the goal's reason names the want: `surgery_part_short` or
`surgery_no_doctor`. The goal settles when the operation leaves the census (the
health change), never when the bill disappears.

Chronic conditions (#1165) join the same ranking under the same 20% cap: a
`cure` recipe, or a replacement-part `install` recipe on the part carrying the
condition (a bionic eye for a cataract), is served when the patient's conditions
include a listed chronic defName (cataract, bad back, frail, dementia, asthma,
hearing loss, and similar). Its value is the capacity the condition costs times
the recipe tier (a cure leaves the natural part, 1.0). Implants never count as a
cure.

Organ harvest (#1169) takes a kidney or a lung, never a heart, liver or last of a
pair and never a harvest native reports lethal, from a prisoner the colony would
not recruit (`PrisonerFacts.HarvestEligible`), within the same 20% cap, one at a
time, and only for a concrete need:

- a colonist's `surgery_part_short` want whose natural install recipe
  (`InstallNaturalKidney`, `InstallNaturalLung`) has no stocked organ. The gain is
  the install's capacity weight times `SilverPerCapacity` (1500 silver); the
  harvested organ then feeds the existing restore or replacement install.
- a silver runway deficit (`SilverShort`: a purchase need and silver below its
  rough price plus the trade silver reserve) with no harvestable organ stocked.
  The gain is the organ's native market value; the routine trade sells a stocked
  organ as surplus while the deficit holds. A short silver runway with an
  eligible prisoner holds the goal open.

The cost is in silver: `SilverPerMoodPoint` (20) times the vanilla thought
magnitude times the colonists it reaches, plus `SilverPerGoodwillPoint` (5) times
the goodwill change native reports. Under `OrganUse_Classic` (no Ideology) every
colonist takes -5; `OrganUse_Horrible*` -4 each and -15 on the surgeon, and
`OrganUse_HorribleNoSell` adds the sale thoughts (-2 each, -8 on the seller) to a
sale; `OrganUse_Acceptable` costs no mood; `OrganUse_Abhorrent` or any unknown
precept refuses. At these prices a classic five-colonist colony harvests a kidney
(900) from a prisoner whose faction loses 70 goodwill (850), and a colony of seven
does not. The harvest queues a medical `ProductionBillIntent` with `acknowledge_violation`.

Electives (#1167, #1843) are bionic or archotech installs on a healthy part. They
need a medical bed, no served operation (restore, cure, chronic replacement) or
queued bill anywhere, a stocked part and a failure chance within 5%
(`ElectiveFailureCap`). Since #1843 the installed part's market value
(`ItemFacts.MarketValue(op.Item)`) must also fit the colonist's remaining
personal share (`SurgeryContext.Elective`, read through
`observation.ColonyProjection.PersonalShareOf`, see
[upkeep contracts](upkeep-contracts.md#personal-wealth-shares-1829-1836)): an
unknown share or an unpriced part is necessities only, so no elective. The gate
carries `ElectiveShareSlack` (10%) of stateless hysteresis, since the part is
already on the map and wealth jitter near its price must not strand it. Affordable
electives from every colonist compete by gain over the natural part x part weight
x role weight, and exactly one is queued colony-wide, ties by pawn id.
`ElectiveSurgeryOwed` applies the same gate, so an unaffordable elective never
holds MaintainSurgery open. Served operations never pass the share gate. The zero
`ElectiveShare` is ungated, which the other `SelectSurgery` callers rely on.

Peg-leg cycling (#1236) takes the same one-surgery slot after harvest and part
recovery. It installs and removes cheap wood parts (peg leg, wooden hand, wooden
foot) on colony prisoners:

- Doctor training: a surgery teaches 1.6 Medicine XP per work tick, 5600 for a
  peg-leg cycle. While fewer doctors than wanted (one, two from eight colonists)
  reach Medicine 10, a cycle is worth 280 silver; above that, 112 while a restore
  waits on a doctor within the failure cap. It costs three medicine, the doctor's
  hour and the removal's goodwill (-70 via `harvest_goodwill_change`, no mood), so
  it runs on factionless, pirate or -100 prisoners. With no wood slot open a
  natural hand, foot or leg (never a second leg) is amputated, its `HarvestCost`
  spread over the cycles the slot supports until the doctor reaches 10. The
  bill is restricted (ProductionBillIntent `surgeon`, #1253) to the lowest-Medicine
  doctor below 10 whose `doctor_chances` entry clears the failure cap; with none,
  vanilla picks. Other surgeries never name a surgeon.
- Prisoner control: a legless prisoner is downed, so no mental, withdrawal or
  prison break. The last peg legs come off a HarvestEligible prisoner not due for
  release, or one in withdrawal, never one being recruited, converted or enslaved,
  when the risk avoided (200, 400 in withdrawal) outweighs the removals,
  hand-feeding (25) and putting both pegs back later.
- Release: a legless prisoner no longer controlled gets a peg leg back first;
  MaintainPopulation holds Release until then, since vanilla cannot release a
  downed pawn.

A pending step holds MaintainSurgery open.

## Medicine selection

The work-assignment routine caps each pawn's medical care (#1301) by class and
medicine stock. A colonist's standing cap is NormalOrWorse while industrial
medicine stock meets the configured per-colonist target, HerbalOrWorse otherwise;
it is never Best as a standing cap. Prisoners being recruited, having resistance
reduced, converted or enslaved get NormalOrWorse; maintain-only, release,
execution and organ-harvest targets get HerbalOrWorse. Hosted guests get
NormalOrWorse. Animals get HerbalOrWorse, or NormalOrWorse when bonded or trained
in Release.

A serious condition raises the cap one tier, so a colonist may reach Best: a
native life threat, Plague, an unimmune WoundInfection, Malaria at severity 0.5
or above, or any immunizable disease losing its immunity race. Harvest and
execution targets are never raised. Unknown facts defer a change.

Care writes send `PawnSettingsIntent.medical_care` (all five tiers) on
Actions/Apply; native checks a living pawn of, or hosted by, the colony when it
applies, and a cap already set applies again. A refused write is replanned from
fresh facts on the next review.

While the medicine reserve is low, it contributes the configured herbal target per colonist to
`MaintainResource`; explicit higher resource floors remain authoritative. The
medical reserve's existing bill and wild-healroot methods continue to work.

## Disease care

When a disease's projected immunity lead over lethal severity is less than a
day, the medical routine enables PatientBedRest and retains rest until the
tracked condition reaches full immunity or leaves a complete census. Native tending, bed selection,
severity progression and immunity gain remain ordinary RimWorld simulation;
the care cap follows the rules above. Missing patients or health
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

A medical `ProductionBillIntent` (`patient` set) on Actions/Apply (#1162) queues one operation bill; see the
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
