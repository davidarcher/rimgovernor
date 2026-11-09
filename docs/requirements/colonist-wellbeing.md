# Keep colonists healthy and able to work

[Collection](README.md) · [Review queue](review-queue.md)

## Intent and evidence ownership

Care has to reach the patient, and rest, recreation, mood and equipment decide
whether people can safely do the work the colony depends on. The
[wellbeing specification](../../openspec/specs/colonist-wellbeing/spec.md) is the standing owner of
RG-HEALTH-001–002 and their scenarios; the maintainer-confirmed policy and its
provenance are in the [decision record](../../openspec/changes/archive/2026-11-05-confirm-remaining-capabilities/proposal.md#decision-provenance-and-review-boundary). Mood is valued by the consequences
it changes, never ignored. This page owns implementation inspection, executed
results and proof limits, not requirements. Its inspection history (W1–W8) is
preserved as recorded.

## Requirement map

Each requirement is stated once, in the specification.

- RG-HEALTH-001: [Treat urgent patients](../../openspec/specs/colonist-wellbeing/spec.md#requirement-rg-health-001--treat-urgent-patients)
- RG-HEALTH-002: [Sustain recovery and everyday wellbeing by consequence](../../openspec/specs/colonist-wellbeing/spec.md#requirement-rg-health-002--sustain-recovery-and-everyday-wellbeing-by-consequence)

## Implementation status of the original requirements

The status below was recorded before the specification owned the requirement text and stays with the evidence it cites.

### Status of RG-HEALTH-001 (treat urgent patients)

**Intent:** Documented in [medical care](../developers/contracts/medical-care.md).
**Implementation:** Supported for the sampled pairing and native tending dispatch;
completed autonomous treatment and replacement-doctor recovery still need native
outcome evidence.
The contract's eligible-patient and care-policy conditions apply. Evidence W1–W3.

### Status of RG-HEALTH-002 (sustain recovery and everyday wellbeing)

**Intent:** Documented in [medical care](../developers/contracts/medical-care.md),
[mood](../developers/contracts/mood-control.md) and
[equipment upkeep](../developers/contracts/equipment-upkeep.md).
**Implementation:** Partial. Condition tracking, selected sleep/recreation,
clothing production and mental-break response are supported. Gatherings and some
residual apparel replacement are held (W7). Successful disease recovery and safe
surgery remain outcome-evidence questions. Evidence W1–W7.

Clothing and care facilities participate in the same comparison by the mood,
protection, recovery and useful time they change, rather than a delivery or
building count. Evidence W8 qualifies these forecasts; the remaining replacement
hold in W7 is unchanged.

## Evidence and inspection coverage

- **W1 — Urgent care path.** [MedicalReview](../../brain/src/RimGovernor.Brain.Runtime/Review/MedicalFamily.cs)
  reads emergency patients and worker facts, routes tending/rescue through the
  critical patient's need and admits work with claims. Its ordinary pass also
  routes hospital, medicine and surgery work. [NativeTendOperations](../../integrations/rimgovernor-native/src/Bridge/Protocol/NativeTendOperations.cs)
  checks the doctor and patient, obtains a legal tending job and verifies that
  the ordered job started. Patient improvement needs a later observation.
- **W2 — Pairing and missing facts.** [MedicalFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/MedicalFamilyTests.cs)
  checks per-patient needs, exclusive doctor/rescuer pairing, busy-worker
  exclusion and missing-health exceptions. Care-policy and work eligibility
  conditions are explicit in these tests. They use supplied observations.
- **W3 — Native eligibility.** Registered OnDemand
  [medical/tend-gates](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Medical/TendGates.cs)
  checks control eligibility and pairwise reachability on native pawn reads.
  It requires at least two reach-checked colonists. It performs no treatment.

Dependent patients follow the population and medical contracts. Patient feeding,
babies, special diets and animal food connect to [food](food-security.md).
### W4 — Recovery, disease and surgery

[MedicalRules.ReviewMedicalCare](../../brain/src/RimGovernor.Brain.Policy/MedicalCare.cs) tracks
patient conditions and unresolved patients. [Disease](../../brain/src/RimGovernor.Brain.Runtime/Review/Disease.cs)
feeds medical rest into work demand and resets history when the world or timeline
changes. Native health observations include severity, immunity and tending.
[medical/plague-readback](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Medical/PlagueReadback.cs)
checks those values against two prepared patients, one tended. The
[surgery cases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Medical/Surgery.cs)
check native operation eligibility, parts, medicine limits and bill handling.
[lab/skill-learning-tend and surgery](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Lab/SkillLearning.cs)
observe native learning callbacks; the surgery callback occurs before its outcome.
None of these assertions guarantees patient survival or successful treatment.

### W5 — Rest, recreation and mental breaks

The shared need forecast selects exact activities; NativePawnActivities dispatches
bed and recreation jobs and observes their lifecycle. Registered OnDemand
[production/coupled-needs](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionCoupledNeeds.cs)
requires sleep, waking and play under the served governor, with rested state
injected between its cycles. [needs/recreation-gain](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Needs/NeedsRecreationGain.cs)
checks legal native recreation targets and gain/capacity inputs.
[Break.ReconcileSubdueFightAsync](../../brain/src/RimGovernor.Brain.Runtime/Review/DefensePlanner.Break.cs)
reads current threats and injuries before ending a subdue attempt and its drafts.
[mood/subdue](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Mood/Subdue.cs)
orders a prepared intervention and requires the target alive, free and downed or
no longer aggressive. Autonomous intervention selection and long-term mood are
separate from that native effect.

### W6 — Useful clothing and interruption recovery

Clothing candidates participate in the shared work comparison, become exact
recipe tasks and run through Hands and native bill execution.
[ApparelDrugPreferencesTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ApparelDrugPreferencesTests.cs)
checks outfit choices and resulting demand.
[production/clothing-activity](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionClothingActivity.cs)
requires a garment of the selected material, conserved inputs and released
participants under the served governor. Its `-unfinished` variant interrupts the
native unfinished garment once and requires resumption. The armor-choice case
[apparel/crafted_armor](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Apparel/CraftedArmor.cs)
checks selected material and forecast harm, without asserting injury reduction.

The inspected paths support these mechanisms. Proof limits are current case
results, final patient outcomes, wearing the manufactured garment and sustained
wellbeing. See [T7](review-queue.md#t7--finish-shelter-and-essential-care).

### W7 — Gatherings and remaining equipment deficits

[RoomsFamily.Propose](../../brain/src/RimGovernor.Brain.Policy/RoomsFamily.cs)
holds autonomous gatherings while participant time and effects lack shared
planning support ([#4001](https://github.com/davidarcher/rimgovernor/issues/4001)).
The native [mood/gathering](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Mood/Gathering.cs)
case explicitly starts a Party and checks the organizer's participation; it
does not remove the planner hold or assert a lasting mood benefit.
[MilitaryReview.EquipmentVerdict](../../brain/src/RimGovernor.Brain.Runtime/Review/Military.cs)
holds remaining apparel deficits after workshop work has no useful next step
([#4009](https://github.com/davidarcher/rimgovernor/issues/4009)). Clothing production
is supported (W6), but the broad promise of recovered worn equipment is partial.
Both holds were lifted on 2026-11-08 (RG-HEALTH-002, RG-EQUIP-002); the two issues
are closed and the assessment re-files the work under the owning specifications.

### W8 — Clothing and care-facility value

[ApparelCreditTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ApparelCreditTests.cs)
checks delivered clothing against nakedness, damage, taint, ideology and
title/role-required apparel thoughts, including minimum quality and the crafter's
quality distribution. Armor changes use the shared harm term; insulation remains
in thermal value. An unwanted garment earns no automatic delivery-count credit.
These are forecast choices, not evidence that every produced garment was worn.

Hospital, dining, recreation and sleeping-room benefits use the same
[room service-day comparison](../../openspec/specs/habitat/spec.md#requirement-rg-habitat-003--develop-facilities-for-the-benefits-they-provide).
Nursery crib rest, play and baby mood also alter caretaker time and the people
exposed to crying or giggles; [K6](colony-commitments.md#k6--care-learning-and-prison-room-consequences)
states the approved approximation limits. Combat-related benefits retain the
[empirical exposure qualifications](protection-and-recovery.md#p6--civilian-exposure-and-forecast-evidence).
The final full gate at `d0fe7897d` passed these policy regressions; it supplies no
new patient-survival, long-term wellbeing or end-to-end clothing acceptance claim.
