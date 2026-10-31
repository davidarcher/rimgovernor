# Medical care contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

Each patient's `medical/critical/<pawn>` Need selects native-approved doctor/patient pairs by bleeding deadline,
native life-threatening state and stable identity. It preserves `NoCare`, player
work overrides and self-tend policy. Assignment review preserves the observed self-tend permission; it does not disable self-treatment because another doctor has a higher nominal quality. Shared care selection compares native-legal treatment options. Explicit native self-tend setting operations and their readback remain available. A completed treatment permits a later tend of
the same patient's Need; a later confirmed, completed autonomous treatment can admit
a new step after fresh native eligibility. Pending and uncertain work cannot.
Actions and receipts stay retained across persistence and method archival. The
current doctor's exact job target distinguishes treatment from tending somebody
else; a receipt never establishes completed treatment.

If triage has no eligible pair after combat clears, the squad's drafts free up
through the undraft sweep once the fight closes and no live plan needs them, and
the patient's critical Need selects treatment on a fresh read. An active or unknown threat
keeps the fight open and the squad drafted. Existing tending is never interrupted.
Changed patient/worker evidence can reopen an unavailable-pair hold.

Confirmed interrupted tending may resume within the recovery bound. An unavailable
doctor can be replaced through a newly validated shared plan step (the original
action and receipt stay retained). The load, native tick, player direction and
native ordered-job generations must match; unknown write results and changed native
player orders never authorize retries. A tend or rescue native refuses is retried
through the shared refusal budget (action-contracts): a permanent refusal gives that
patient up under native's reason, a transient one waits for the world to change, and the
clock still advances under the standing deficit.

## the brain planning

The `medical` family (`Medical.cs`, runtime `MedicalReview`, #3258) derives one
`medical/critical/<pawn>` Need (SeriousHarm) per critical patient: a living colonist
or dependent who is bleeding, downed or needs a tend, or whom a life-saving
amputation would save. The pawn and medical censuses are total. Native reads every spawned colonist each frame, without a cap; missing sections, unresolved colonist references and disagreeing rosters fail the read. A stale emergency census fails the planner boundary. The Needs run in the immediate pass on the emergency census
and the doctors' and patients' pawn rows alone. Every patient's Need reads the same
greedy pairing (`TendPairs`, `RescuePairs`) over the pawns no open tend or rescue
holds, takes its own pair, and commits `tend-<patient>` or `rescue-<patient>` under
the Need owner. A tend or rescue claims both pawns (`care/<pawn>`), so two patients'
tends run side by side and an order that would take a held pawn is a claim conflict.
The exact tend answers an observed emergency only (RG-LABOR-004, task 5.5 of
`native-led-labor-execution`): the pairing takes a patient who is downed, bleeding
toward a finite death or carrying a life-threatening condition
(`TendRules.Emergency`). A patient who only needs tend (in bed, neither bleeding
out nor life-threatened) is ordinary native Doctor work: no pair is ordered, and
the Need waits with `ordinary_tend_native_doctor_work`. The emergency is the
premise and the patient's tended state the completion, so the order is a bounded
intervention without the coupled justification gate; rescue is unchanged.
In the ordinary pass a patient's Need first queues a life-saving amputation
(`amputate-<patient>-<part>-<window>`).

Combat formation leaves the pawns held by these open care orders to their medical
Method, including across scheduler steps. In a wave selecting both planners,
defense starts after the medical family returns, so a care read still in flight
cannot claim a pawn between combat's availability read and formation admission.
The dependency waits outside the planner concurrency limit, including at width
one. A failed medical run releases the dependency too: defense reads the actual
claims and forms from the available defenders. A wave that does not select medical
does not wait for it. It also excludes the current step's
claimed pawns before selecting fighters and shelter roles. All observed pawn and
threat facts remain in the combat view for safety decisions. Other available
defenders can therefore admit a fight while rescue or tending proceeds; a care
claim on one pawn does not refuse the whole colony's defense.

The executor's emergency gate (`policy.EvaluateEmergency`) holds dispatch as
`critical_medical` on the same terms: a bleeding or downed-untended colonist, or an
unknown health fact, holds every action. A colonist who only needs tending is the
tend planner's patient, one downed with nothing to tend is the rescue planner's,
and neither holds anything. The combat watch acknowledges every colonist known
downed so the native watcher lets rescue and recovery proceed without holding again.

## Dependent patients

Each dependent patient's `medical/critical/<pawn>` Need owns eligible tending and rescue
for the native dependent-patient census. It includes nonhostile humanlike hosted guests,
spawned subjects of active refugee-pod rescue objectives, and entities currently
held on a player holding platform. Free colonists remain in their original census;
ordinary prisoners, hostile acquisition targets and slaves are not added by this
adapter. An active rescue objective may retain a housed prisoner as a tend patient.

Each dependent carries explicit rescue and tend applicability. Held entities are
tend-only. An unhoused nonhostile rescue objective is rescue-eligible; tending
becomes applicable when hosted. Native health, bed, care, reachability and threat
checks still decide whether an exact order is legal. Existing urgency thresholds
are unchanged. The patient union does not enlarge the colonist doctor, rescuer or
combat roster. Downed patient acknowledgements use the same observed identities.

A missing census or unresolved colonist reference is a contract error, never an empty or recovered need. Native capability and health states retain their explicit applicability. Duplicate membership and
unknown population groups are contract errors. The native frame adds health detail
for these patients through the existing pawn table; no patient store is introduced.
Neither the prisoners nor the anomaly family dispatches a parallel rescue or tend Method for these patients (`prisoners/custody` and `anomaly/tend` hold them). Capability-approved critical care uses the patient's critical Need; noncritical dependent care and unsupported custody transport remain unmet pending shared forecast support.

## Maintained care

[Support work](support-work.md#room-consequences) compares conditional treatment work
using native eligibility, existing medical demand and the room as it stands.
Ordinary tending and already-indicated surgery use native Doctor priorities,
care policies and surgical bills. A selected hypothetical treatment is not an
exact order or a completed tend. Native support assessment preserves current
demand and leaves future timing/outcomes unprojected. The separate critical
medical Need above continues to own its bounded emergency interventions and
receipts. An ordinary tend becomes an exact order only once its demand is observed
unserved over a whole care window ([support work](support-work.md#shared-support-methods):
the `UnservedCare` premise and its `unserved_care` purpose); a patient being
served, or merely needing tend, re-admits nothing.
It does not choose surgical indications or invent an infection prognosis. Vanilla
cleans the room under the Cleaning priority; there is no coupled cleaning and no
automatic cleaning finalizer.

The `medical/care` Need (Wellbeing, unmet while a sick colonist rests or is in bad
condition, unmet while a previously tracked pawn is absent or dead) retains native visible condition
identities, severity, immunity, retend timing, care policy and medical-rest state.
It enables ordinary Patient and PatientBedRest work when available and not
disabled; native jobs choose beds. Unavailable work and player restrictions produce explicit blockers; missing native observations fail the read. Routine reads and direct colony reads derive medical, mood and work profiles from the same complete pawn table. The same-tick native census separates
chronic conditions from urgent tending.

- Tracked patient identities persist through Manual and restart. Missing or dead
  patients and incomplete condition lists never certify recovery. World replacement
  and tick rewind clear the history.
- Per-condition the brain Facts carry severity, immunity, tended state, tend quality, and
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
are never raised. Invalid or unavailable medicine catalog data fails the review. Snapshot test
`TestSnapshotDiseaseSelectsIndustrialCare` pins the Plague case (two patients,
industrial medicine in stock: both and only they go to NormalOrWorse).

Care writes send `PawnSettingsIntent.medical_care` (all five tiers) on
Actions/Apply; native checks a living pawn of, or hosted by, the colony, and a cap
already set applies again. A refused write is replanned from fresh facts on the
next review.

The `medical/reserves` Need (Wellbeing) is unmet while the usable medicine stock is
below its per-colonist target (no entry bar), fails the read if native stock or catalog facts are unavailable. Its
work withdraws stalled healroot designations and harvests wild healroot under the
Need owner; its stock-target bill is the work ledger's. While the medicine reserve
is low it contributes the configured herbal target per colonist to
`economy/resource`; explicit higher floors stay authoritative. Care and reserves
are independent Needs: the stock never waits on the sick resting.

## Surgery

### Facts and bills

Surgery facts ride on the per-pawn care read (`PawnHealth.missing_parts`,
`PawnHealth.operations`). Native discovers recipes through
`RecipeWorker.GetPartsToApplyOn`/`AvailableOnNow` and computes `success_chance` with
the recipe's own `SurgeryOutcomeEffectDef` comps for the best eligible doctor,
substituting the best colony medical bed when the patient is not in bed and the best
permitted medicine on the map. It also reports the eligible doctor count, whether
ingredients and medicine are on the map, vanilla `IsViolationOnPawn`, and `lethal`
(`WouldDieAfterAddingHediff`, or an execution). The brain maps them into `policy.CarePawn`
and does no surgery math.

A medical `ProductionBillIntent` (`patient` set) on Actions/Apply queues one
operation bill; see the `surgery` row of [action contracts](action-contracts.md).
The native setup used by medical acceptance is excluded from production builds and
from the model execution surface.

### medical/surgery

Restores missing or destroyed parts. Per part it takes the best recipe whose
ingredients are on the map (bionic, then prosthetic, then peg) that some eligible
doctor performs with a native failure chance of 20% or less. Each patient queues its
most valuable part (capacity weight times part tier) as one medical
`ProductionBillIntent`, and gets no second while a bill is queued or a surgery action
is open. Otherwise the reason names the want: `surgery_part_short` or
`surgery_no_doctor`. The Need is met when the operation leaves the census (the
health change), never when the bill disappears. A pending step keeps it unmet.

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
  while the deficit holds. A short runway with an eligible prisoner keeps the Need
  unmet.

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
medical bed, a stocked part and a failure chance within 5% (`ElectiveFailureCap`). No
wealth budget and no colony-wide one-at-a-time rule limits them: the doctor's ability,
the stocked part and the patient's own state do. A patient with a served operation
(restore, cure, chronic replacement) or a queued bill gets no elective. Each patient's
best elective ranks by gain over the natural part x part weight x role weight; ties go to
the lower pawn id, and a part item goes to one patient per review. The stock read is a
bool, so two bills queued in different reviews can still race for one part.

**Elective part demand.** `policy.ChosenElectives(pawns, ctx)` is each patient's best
elective whose part is missing (same ranking as `SelectSurgery`, over stocked and missing
parts alike) that a doctor performs within `ElectiveFailureCap`, with a part item wanted
by one patient at most. A patient whose best part has an option on the map wants nothing
here: `SelectSurgery` queues it. `ElectiveParts` turns the wants into `SurgeryPart`s of
the items some usable bench fabricates, appended after the served parts, so the existing
`SurgeryPartBill` (fabricate-first, `FabricableParts`) builds them; a part nothing
fabricates yields no demand there. `ElectiveSurgeryOwed` keeps `medical/surgery` unmet
for a chosen elective only while it is fabricable. Demand clears when the part is
stocked, installed or queued. The trade side
(`Trade.cs`) sees served parts only.

**Elective part purchase.** For each chosen elective no item of which can be fabricated,
`policy.SurgeryPurchaseParts` adds its part to the trade part demand beside the served
parts. `surgeryPartTargets` buys one unit (MaxBuy 1) with no unit price ceiling, under
the trade-wide silver reserve.

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
life-threatening condition. The brain requests monitoring only from current
health observations and an active medical-care concern. Native `medicalRestIds` windows
are limited to 600 ticks and recheck each patient; changed eligibility stops the
window for another review. This permits feeding and rest without certifying recovery
or satisfying the medical stability gate.
