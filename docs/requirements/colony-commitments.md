# Sustain people, animals and accepted commitments

[Collection](README.md) · [Review queue](review-queue.md)

## Intent and evidence ownership

New people, prisoners, quests and colony practices bring continuing obligations.
The [commitments specification](../../openspec/specs/colony-commitments/spec.md) is the standing
owner of RG-COMMIT-001 and RG-COMMIT-003 and their scenarios; the maintainer-confirmed
policy and its provenance are in the [decision record](../../openspec/changes/archive/2026-11-08-define-equipment-and-commitments/proposal.md#decision-provenance-and-review-boundary). RG-COMMIT-002 lives
in the [herd management specification](../../openspec/specs/herd-management/spec.md) since
2026-11-03. This page owns implementation inspection, executed results and proof
limits, not requirements. Its inspection history (K1–K6) is preserved as recorded.

## Requirement map

Each requirement is stated once, in the specification.

- RG-COMMIT-001: [Take on people and quests by priced contribution](../../openspec/specs/colony-commitments/spec.md#requirement-rg-commit-001--take-on-people-and-quests-by-priced-contribution)
- RG-COMMIT-002: [Carry out and observe animal care](../../openspec/specs/herd-management/spec.md#requirement-rg-commit-002--carry-out-and-observe-animal-care) (herd management)
- RG-COMMIT-003: [Advance supported colony practices and abilities](../../openspec/specs/colony-commitments/spec.md#requirement-rg-commit-003--advance-supported-colony-practices-and-abilities)

## Implementation status of the original requirements

The status below was recorded before the specification owned the requirement text and stays with the evidence it cites. The holds it names (K4–K5) are defects to remove under the 2026-11-08 decision, not known gaps.

### Status of RG-COMMIT-001 (match commitments to available capacity)

**Intent:** Documented in [population](../developers/contracts/population-contracts.md),
[husbandry](../developers/contracts/husbandry-contracts.md) and
[quests](../developers/contracts/quests.md). **Implementation:** Partial. Capacity
decisions and supported quest objectives are connected. Autonomous population
capture and bestowing are explicitly held (K4–K5).

### Status of RG-COMMIT-003 (advance supported colony practices and abilities)

**Intent:** Formerly an inferred candidate; confirmed by the maintainer on 2026-11-08.
**Implementation:** Partial. Role and conversion paths exist. Rituals, bestowing,
neuroformer use and monolith interaction have confirmed holds. Evidence K5.

## Evidence and inspection coverage

- **K1 — Capacity conditions.** The population contract requires a food reserve
  that increases with hosted population, accounts separately for guests and
  prisoners, and allows temporary sleeping places while beds are built. The quest
  contract protects essential workers and describes objective-specific costs,
  routes and completion. K4 traces representative decisions and objective work.
  Fixed targets in those contracts are implementation policy,
  pending any explicit product decision to make them lasting requirements.
- **K2 — Accepting a quest.** Registered OnDemand
  [quest/accept](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Quest/QuestAccept.cs)
  checks a real native quest acceptance and reward choice, refusals for invalid
  choices and replay behavior. It begins with a prepared offer and does not
  evaluate whether accepting it benefits the colony or completes its objectives.
- **K3 — Animal path.** [AnimalsReader](../../brain/src/RimGovernor.Brain.Runtime/Review/Animals.cs)
  reads herd and room facts and routes containment and ward work through the
  shared building planner. Registered Smoke
  [husbandry/dispatch](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Husbandry/HusbandryDispatch.cs)
  applies animal settings and reads them back, including training, taming,
  slaughter, release, areas and following. Invalid combinations are refused.
  These immediate settings checks stop before animal jobs finish.

### K4 — Capacity, objective work and custody

[QuestPolicyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/QuestPolicyTests.cs)
checks joiner food/bed capacity, outgunned or unread offers, production-decree
quantities and materials, and shuttle loading before launch.
[Joiners](../../brain/src/RimGovernor.Brain.Runtime/Review/JoinersReview.Joiners.cs)
routes accepted objectives through site, hack, expedition and hospitality work
under their claims, retaining pending work. Native
[quest/hack](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Quest/QuestHack.cs)
requires a prepared hack to finish; [quest/shuttle](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Quest/QuestShuttle.cs)
requires boarding and departure. The fixtures arrange the objective locally;
neither establishes autonomous travel and full quest fulfilment.
[custody cases](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Custody/CustodyPopulation.cs)
exercise explicit capture/arrest and observe custody and bed placement.
AnimalsReader feeds herd demand and pen/ward work into shared planning;
[AnimalsIdeologyFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/AnimalsIdeologyFamilyTests.cs)
checks containment, ward and sterilization conditions and unread herd facts.

### K5 — Native operations with missing autonomous selection

[IdeologyFamily.Propose](../../brain/src/RimGovernor.Brain.Policy/IdeologyFamily.cs)
supports role/conversion proposals and holds rituals; its reader retains pending
reform intent. [ideology/first-ritual](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Ideology/IdeologyRitual.cs)
explicitly starts a native ritual and requires a later completion tick.
That fixture bypasses the autonomous hold tracked in
[#3580](https://github.com/davidarcher/rimgovernor/issues/3580).

[JoinersFamily](../../brain/src/RimGovernor.Brain.Policy/Joiners.cs),
[PrisonersFamily](../../brain/src/RimGovernor.Brain.Policy/Prisoners.cs),
[RoyaltyFamily](../../brain/src/RimGovernor.Brain.Policy/RoyaltyFamily.cs) and
[AnomalyFamily](../../brain/src/RimGovernor.Brain.Policy/AnomalyFamily.cs) expose
the remaining limits: bestowing [#4002](https://github.com/davidarcher/rimgovernor/issues/4002),
population capture/lance use [#4003](https://github.com/davidarcher/rimgovernor/issues/4003),
neuroformers [#4004](https://github.com/davidarcher/rimgovernor/issues/4004) and
monolith interaction [#4005](https://github.com/davidarcher/rimgovernor/issues/4005).
Medical rescue and tending use their own care path. Royalty permits have a
selection method; that does not lift the neuroformer hold. Those four issues were
closed on 2026-11-08 when RG-COMMIT-003 lifted the holds; the assessment re-files
the work under that specification. These are current
implementation limits, with desired admission policy still to decide.

Remaining proof limits are sustained population/herd support, autonomous remote
quest completion and the benefits of completed ceremonies.
Food and health obligations link to [food](food-security.md) and
[care](colonist-wellbeing.md). See
[T10](review-queue.md#t10--carry-one-commitment-through-to-its-outcome).

### K6 — Care, learning and prison-room consequences

The [room requirement](../../openspec/specs/habitat/spec.md#requirement-rg-habitat-003--develop-facilities-for-the-benefits-they-provide)
also covers facilities used by children, babies, prisoners and DLC dependants.
Their value must reflect eligible occupants and shared worker time, rather than
the mere presence of a classroom, nursery, cell or requirement room.

[ClassroomCreditTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ClassroomCreditTests.cs)
checks teaching costs and pupils' skill benefits, including native maturation,
unlocked work and work speed within the existing investment horizon. It does not
extend that horizon to give very young children an assumed later payoff.
[PrisonCreditTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PrisonCreditTests.cs)
checks room mood, memories, conversion before recruitment, warden travel/chat
costs and shared inspiration consumption. A prisoner the governor will not recruit
earns no recruitment benefit. The approved joint calculation keeps at most 4,096
states; discarded probability receives no recruitment benefit and the maximum
remaining chat cost, with uncertainty reported. One inspiration cannot recruit
several prisoners.

[NurseryCreditTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/NurseryCreditTests.cs),
[NurserySleepTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/NurserySleepTests.cs)
and [NurseryGeometryTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/NurseryGeometryTests.cs)
connect crib rest, play, baby mood, disturbed sleep and social consequences to
the shared forecast. Hearing and interaction use activity locations and native
eligibility, including eligible prisoners, guests and slaves without adding them
to the worker census. Approved future-room sound and walking-play exposure bounds
credit only guaranteed benefits. The opinion/mood/crying feedback uses an explicitly
qualified expected-value approximation, without a certified small error bound.

These policy regressions passed in the final full gate at `d0fe7897d`; this is not
evidence of a completed native education or recruitment campaign. Existing observed
prisoner death memories age normally. New deaths require the separate prisoner
mortality model in [#4014](https://github.com/davidarcher/rimgovernor/issues/4014),
explicitly excluded from epic #3650 by the user. Colonist casualty rates are not
substituted for prisoner mortality. The other autonomous progression holds in K5 remain separate.
