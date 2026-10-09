# Medical care contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`CriticalMedical` selects native-approved doctor/patient pairs by bleeding deadline,
native life-threatening state and stable identity. It preserves `NoCare`, player
work overrides and self-tend policy. A completed treatment permits a later tend in
the same concern episode; a later confirmed, completed autonomous treatment can admit
a new step after fresh native eligibility. Pending and uncertain work cannot.
Actions and receipts stay retained across persistence and method archival. The
current doctor's exact job target distinguishes treatment from tending somebody
else; a receipt never establishes completed treatment.

If triage has no eligible pair after combat clears, the squad's drafts free up
through the undraft sweep once the fight closes and no live plan needs them, and
`CriticalMedical` selects treatment on a fresh read. An active or unknown threat
keeps the fight open and the squad drafted. Existing tending is never interrupted.
Changed patient/worker evidence can reopen an unavailable-pair hold.

Confirmed interrupted tending may resume within the recovery bound. An unavailable
doctor can be replaced through a newly validated shared plan step (the original
action and receipt stay retained). The load, native tick, player direction and
native ordered-job generations must match; unknown write results and changed native
player orders never authorize retries.

## Go planning

`CriticalMedicine` is a priority-1 emergency only
while a critical patient is bleeding or downed with a tend outstanding, or that
count is unknown (`policy.UrgentPatients`). A living colonist who merely needs
tending (chronic conditions included), or who is downed with nothing to tend
(malnutrition, exhaustion, a tended wound), keeps the concern active at priority 2:
the same tend and rescue methods serve them while the colony's other work and its
clock go on.

The executor's emergency gate (`policy.EvaluateEmergency`) holds dispatch as
`critical_medical` on the same terms: a bleeding or downed-untended colonist, or an
unknown health fact, holds every action. A colonist who only needs tending is the
tend planner's patient, one downed with nothing to tend is the rescue planner's,
and neither holds anything. The clock window acknowledges every colonist known
downed so the native watcher lets rescue and recovery take ticks.

## Maintained care

`MaintainMedicalReserves` (care phase, priority 2) retains native visible condition
identities, severity, immunity, retend timing, care policy and medical-rest state.
It enables ordinary Patient and PatientBedRest work when available and not
disabled; native jobs choose beds. Missing observations, unavailable work or player
restrictions produce explicit blockers. The same-tick native census separates
chronic conditions from urgent tending.

- Tracked patient identities persist through Manual and restart. Missing or dead
  patients and incomplete condition lists never certify recovery. World replacement
  and tick rewind clear the history.
- Per-condition Go Facts carry severity, immunity, tended state, tend quality, and
  instantaneous severity/day and immunity/day (60000 game ticks). Severity/day
  includes native immunizable and tending modifiers; immunity/day uses the native
  immunity record. Missing fields stay unknown, including rates for conditions
  without an immunizable component or immunity record. They are current facts,
  not prognosis.
- Stable chronic monitoring does not time out completed work settings; pending work
  keeps the normal watchdog.
- When a disease's projected immunity lead over lethal severity is under a day, the
  routine enables PatientBedRest and retains rest until the tracked condition reaches
  full immunity or leaves a complete census. Death is a failure.

## Medicine selection

The work-assignment routine caps each pawn's medical care by class and medicine
stock:

| Subject | Cap |
| --- | --- |
| Colonist | NormalOrWorse while industrial medicine meets the per-colonist target, else HerbalOrWorse; never Best as a standing cap |
| Prisoner being recruited, resistance-reduced, converted or enslaved | NormalOrWorse |
| Prisoner for maintain-only, release, execution or organ harvest | HerbalOrWorse |
| Hosted guest | NormalOrWorse |
| Animal | HerbalOrWorse; NormalOrWorse when bonded or trained in Release |

A serious condition raises the cap one tier, so a colonist may reach Best: a native
life threat, Plague, an unimmune WoundInfection, Malaria at severity 0.5 or above,
or any immunizable disease losing its immunity race. Harvest and execution targets
are never raised. Unknown facts defer a change. Snapshot test
`TestSnapshotDiseaseSelectsIndustrialCare` pins the Plague case (two patients,
industrial medicine in stock: both and only they go to NormalOrWorse).

Care writes send `PawnSettingsIntent.medical_care` (all five tiers) on
Actions/Apply; native checks a living pawn of, or hosted by, the colony, and a cap
already set applies again. A refused write is replanned from fresh facts on the
next review.

While the medicine reserve is low it contributes the configured herbal target per
colonist to `MaintainResource`; explicit higher floors stay authoritative. The
reserve's bill and wild-healroot methods continue to work.

## Surgery

### Facts and bills

Surgery facts ride on the per-pawn care read (`PawnHealth.missing_parts`,
`PawnHealth.operations`). Native discovers recipes through
`RecipeWorker.GetPartsToApplyOn`/`AvailableOnNow` and computes `success_chance` with
the recipe's own `SurgeryOutcomeEffectDef` comps for the best eligible doctor,
substituting the best colony medical bed when the patient is not in bed and the best
permitted medicine on the map. It also reports the eligible doctor count, whether
ingredients and medicine are on the map, vanilla `IsViolationOnPawn`, and `lethal`
(`WouldDieAfterAddingHediff`, or an execution). Go maps them into `policy.CarePawn`
and does no surgery math.

A medical `ProductionBillIntent` (`patient` set) on Actions/Apply queues one
operation bill; see the `surgery` row of [action contracts](action-contracts.md).
The native setup used by medical acceptance is excluded from production builds and
from the model execution surface.

### MaintainSurgery (priority 2)

Restores missing or destroyed parts. Per part it takes the best recipe whose
ingredients are on the map (bionic, then prosthetic, then peg) that some eligible
doctor performs with a native failure chance of 20% or less. Each patient queues its
most valuable part (capacity weight times part tier) as one medical
`ProductionBillIntent`, and gets no second while a bill is queued or a surgery action
is open. Otherwise the reason names the want: `surgery_part_short` or
`surgery_no_doctor`. The concern settles when the operation leaves the census (the
health change), never when the bill disappears. A pending step holds it open.

**Chronic conditions** join the same ranking under the same 20% cap: a `cure`
recipe, or a replacement-part `install` recipe on the part carrying the condition
(a bionic eye for a cataract), is served when the patient has a listed chronic
defName (cataract, bad back, frail, dementia, asthma, hearing loss, and similar).
Value is the capacity the condition costs times the recipe tier (a cure leaves the
natural part, 1.0). Implants never count as a cure.

**Organ harvest** takes a kidney or a lung, never a heart, liver, last of a pair or
a harvest native reports lethal, from a prisoner the colony would not recruit
(`PrisonerFacts.HarvestEligible`), within the 20% cap, one at a time, and only for a
concrete need:

- A colonist's `surgery_part_short` want whose natural install recipe
  (`InstallNaturalKidney`, `InstallNaturalLung`) has no stocked organ. Gain is the
  install's capacity weight times `SilverPerCapacity` (1500 silver); the harvested
  organ then feeds the existing install.
- A silver runway deficit (`SilverShort`: a purchase need and silver below its rough
  price plus the trade silver reserve) with no harvestable organ stocked. Gain is the
  organ's native market value; the routine trade sells a stocked organ as surplus
  while the deficit holds. A short runway with an eligible prisoner holds the concern
  open.

Cost in silver: `SilverPerMoodPoint` (20) times the vanilla thought magnitude times
the colonists it reaches, plus `SilverPerGoodwillPoint` (5) times the goodwill change
native reports. Under `OrganUse_Classic` (no Ideology) every colonist takes -5;
`OrganUse_Horrible*` -4 each and -15 on the surgeon; `OrganUse_HorribleNoSell` adds
the sale thoughts (-2 each, -8 on the seller) to a sale; `OrganUse_Acceptable` costs
no mood; `OrganUse_Abhorrent` or any unknown precept refuses. At these prices a
classic five-colonist colony harvests a kidney (900) from a prisoner whose faction
loses 70 goodwill (850); a colony of seven does not. The harvest queues a medical
`ProductionBillIntent` with `acknowledge_violation`.

**Electives** are bionic or archotech installs on a healthy part. They need a
medical bed, no served operation (restore, cure, chronic replacement) or queued bill
anywhere, a stocked part and a failure chance within 5% (`ElectiveFailureCap`). The
part's market value (`ItemFacts.MarketValue(op.Item)`) must also fit the colonist's
remaining personal share (`SurgeryContext.Elective`, read through
`observation.ColonyProjection.PersonalShareOf`, see
[upkeep contracts](upkeep-contracts.md#personal-wealth-shares)): an
unknown share or unpriced part is necessities only, so no elective. The gate carries
`ElectiveShareSlack` (10%) of stateless hysteresis, since the part is already on the
map and wealth jitter near its price must not strand it. Affordable electives from
every colonist compete by gain over the natural part x part weight x role weight;
exactly one is queued colony-wide, ties by pawn id. `ElectiveSurgeryOwed` applies the
same gate, so an unaffordable elective never holds MaintainSurgery open. Served
operations never pass the share gate; the zero `ElectiveShare` is ungated, which the
other `SelectSurgery` callers rely on.

**Elective part demand.** `policy.ChosenElective(pawns, ctx)` is the one elective
colony-wide whose part is missing: the best affordable elective (same gate, slack and
ranking as `SelectSurgery`, over stocked and missing parts alike) that a doctor
performs within `ElectiveFailureCap`. It is none while electives are not allowed (a
served operation anywhere wins) or once any option of the chosen part is on the map,
when `SelectSurgery` queues it. `ElectiveParts` turns it into a `SurgeryPart` of the
items some usable bench fabricates, appended after the served parts, so the existing
`SurgeryPartBill` (fabricate-first, `FabricableParts`) builds it; a part nothing
fabricates yields no demand there. `ElectiveSurgeryOwed` holds MaintainSurgery open
for a chosen elective only while it is fabricable. Demand clears when the part is
stocked, installed, queued or no longer affordable. The trade side
(`rounds_trade.go`) sees served parts only.

**Elective part purchase.** When no item of the chosen elective can be fabricated and
no served part purchase is pending, `policy.SurgeryPurchaseParts` adds its part to the
trade part demand. `surgeryPartTargets` buys one unit (MaxBuy 1) under
`surgeryPartPriceCeiling` and the trade-wide silver reserve. The share gate lives in
`ChosenElective`, so an unaffordable elective demands nothing and the concern recovers.

Acceptance: `medical/surgery-elective-rich` and `medical/surgery-elective-poor` share a
hospital, three Medicine 20 doctors, a third colonist missing a leg with a prosthetic
stocked (the one served operation) and a stocked BionicEye and BionicArm
(`test/medical_management_setup` `condition=elective`, `wealth=rich|poor`: 6000 gold,
or every loose item but wood, medicine, those parts and eight meals destroyed). Both
assert served first (no elective intent before the leg's bill, the leg installed by
the end). Rich asserts exactly one elective intent at first sight, no colonist with
two queued elective bills, and a bionic installed after one served window. Poor
asserts no elective is ever queued or installed and MaintainSurgery recovers.

**Peg-leg cycling** takes the same one-surgery slot after harvest and part recovery.
It installs and removes cheap wood parts (peg leg, wooden hand, wooden foot) on colony
prisoners:

- Doctor training: a surgery teaches 1.6 Medicine XP per work tick, 5600 for a
  peg-leg cycle. While fewer doctors than wanted (one, two from eight colonists)
  reach Medicine 10, a cycle is worth 280 silver; above that, 112 while a restore
  waits on a doctor within the failure cap. It costs three medicine, the doctor's hour
  and the removal's goodwill (-70 via `harvest_goodwill_change`, no mood), so it runs
  on factionless, pirate or -100 prisoners. With no wood slot open a natural hand,
  foot or leg (never a second leg) is amputated, its `HarvestCost` spread over the
  cycles the slot supports until the doctor reaches 10. The bill is restricted
  (`ProductionBillIntent` `surgeon`) to the lowest-Medicine doctor below 10 whose
  `doctor_chances` entry clears the failure cap; with none, vanilla picks. Other
  surgeries never name a surgeon.
- Prisoner control: a legless prisoner is downed, so no mental, withdrawal or prison
  break. The last peg legs come off a HarvestEligible prisoner not due for release, or
  one in withdrawal, never one being recruited, converted or enslaved, when the risk
  avoided (200, 400 in withdrawal) outweighs the removals, hand-feeding (25) and
  putting both pegs back later.
- Release: a legless prisoner no longer controlled gets a peg leg back first;
  MaintainPopulation holds Release until then, since vanilla cannot release a downed
  pawn.

### Clock allowances

The supervised clock accepts short-lived surgical recovery IDs only from confirmed,
current-direction patient operations in the current colony/load/map. Native sweeps
permit downing only while the identified patient is alive, anesthetized, in a bed,
not bleeding, not dangerously ill and above half health. Injury and death guards stay
active. The allowance expires at the configured work window; it is not a general
exemption for downed patients. Changed context invalidates surgical completion
tracking, and stalled operations expose a bounded no-progress failure.

Stable downed patients may share survival priority while ordinary caregivers work.
Native `stableRestEligible` requires a living, undrafted colonist in bed, above half
health, without bleeding, a current tending need, a mental state, anesthesia or a
life-threatening condition. The controller requests monitoring only from current
health observations and an active medical-care concern. Native `medicalRestIds` windows
are limited to 600 ticks and recheck each patient; changed eligibility stops the
window for another review. This permits feeding and rest without certifying recovery
or satisfying the medical stability gate.
