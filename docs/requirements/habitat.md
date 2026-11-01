# Provide a habitable colony

[Collection](README.md) · [Review queue](review-queue.md)

Shelter becomes useful when people can occupy it and its services work. The
governor may revise every room, building and zone as the colony's needs change.

## RG-HABITAT-001 — Give colonists usable sleeping space

The governor should provide sleeping places and develop enclosed, roofed rooms
with beds for the colony. Temporary sleeping spots should bridge construction
delays. Population changes and lost shelter should lead to a fresh assessment.
The purpose is a usable home through both initial settlement and later growth.

With three colonists, materials and eligible builders, a first sleeping room
should finish and each colonist should own a bed inside it. If construction is
blocked, the need should remain visible with its cause. A new arrival should
receive a temporary place while permanent capacity catches up.

**Intent:** Documented in [spatial](../developers/contracts/spatial-contracts.md)
and [population](../developers/contracts/population-contracts.md) contracts.
**Implementation:** Supported for sampled first-room construction and changing
layout inputs. Repeated growth/loss recovery needs broader execution evidence.
Milestone 2 extends accommodation beyond colonists: a hosted guest or slave without a
suitable bed stays a sleeping target and is built a bed; every occupant needs a
compatible reachable slot of its own (a prisoner a prison bed, a baby a crib, a slave
a slave a slave bed); accepted hosting quests' lodgers are dated expected changes, prepared only when a priced
shortage exceeds the cost ([H13](#h13--fields-herds-prospective-capacity-and-utilities-compared-as-priced-consequences)); shared,
private and held barracks are compared as layout alternatives; furnishing is built
only when its owners' gain beats its cost; and each herd's open pen, barn and
animal sleeping spot are compared by exposure deaths avoided
([H9](#h9--hosted-occupants-bed-sharing-and-herd-shelter)). Milestone 6 reads the human pregnancy fact,
dates a birth, a baby's leaving the crib and a lodger's arrival and departure, and sizes the nursery with hosted
babies (H13). Barn execution (the animal flap, owner step, spot assignment and heater) and the room-quality
minimums remain gaps. Milestone 4 adds that a medical bed is no free sleeping slot, a
patient held in one counts once, and a bed a care transport holds is its patient's
([H11](#h11--care-locations-compared-by-readiness-movement-delay-and-outcome)).
Milestone 8 holds an occupied bed until its replacement is observed assignable and reachable, and relocates a minifiable bed onto its slot as one native reinstall ([H15](#h15--executable-redevelopment-and-retirement-milestone-8)). Evidence H1–H2, H6, H9, H11, H13, H15.

## RG-HABITAT-002 — Keep essential facilities usable

The governor should provide the power, fuel, temperature control and upkeep
needed by occupied rooms and useful facilities. Service demand should reflect
what the colony needs to operate. Reassessment should distinguish a fuel shortage,
damage and insufficient generating capacity so the chosen work addresses the cause.

An empty generator with adequate capacity and available fuel should be refuelled.
A damaged facility should remain deficient until an observation shows restoration.
A cold or hot sleeping room should create temperature work. Where resources are
unavailable, the affected service and blocker should remain visible.

**Intent:** Documented in [power](../developers/contracts/power-contracts.md),
[upkeep](../developers/contracts/upkeep-contracts.md) and spatial contracts.
**Implementation:** Partial. Fuel, roof/temperature, cooling and Home-area
maintenance are supported. Milestone 5 compares a lost producer's refuel, fuel
acquisition and breakdown fix, and a damaged building's repair, fix, door-to-wall
replacement, retirement or deferral, as measured servicing work; the Need stays unmet
until fresh facts show restoration ([H12](#h12--servicing-and-power-restoration-compared-as-measured-work)).
Turret and other non-power service value, chemfuel and trade fuel acquisition and a
component source are not modelled. Milestone 6 compares generation, storage, roofing, routing, consumer
configuration and passive thermal provision by the interruption each consumer's loss prices, and orders power
switches and a forbidden producer's allow under Auto ([H13](#h13--fields-herds-prospective-capacity-and-utilities-compared-as-priced-consequences)).
Resilience across repeated losses needs further evidence. A hosted guest's or slave's cold or hot sleeping room is
temperature work like a colonist's (H9). Milestone 8 holds a generator until its replacement is observed powering the net ([H15](#h15--executable-redevelopment-and-retirement-milestone-8)). Evidence H3–H6, H12, H13, H15.

## RG-HABITAT-003 — Develop rooms for the benefit they provide

The governor should compare room construction with other useful work by the
consequences the finished room changes for this colony. A room should gain value
from when its required work is complete, after accounting for materials, shared
workers and other rooms already under construction. Completing an arbitrary
number of buildings, floors or removals is not itself a benefit.

A workshop should be useful because its stations improve needed production; a
prison cell because its conditions improve a wanted prisoner's recruitment after
any required conversion and warden work. A classroom should gain only the useful
learning its pupils can realize within the existing investment horizon. A room
with nobody eligible to use it can have a known zero benefit. An unread required
fact must prevent that option from being priced, rather than inventing a gain.

Floors and removals should contribute through the rooms they enable. Corridors
and airlocks should contribute through the rooms they reach or seal. Sharing a
room should weigh changed room benefits, disturbed sleep, walls and travel;
membership in a fixed room-compatibility table should not decide it.

**Intent:** Human-approved in [epic #3650](https://github.com/davidarcher/rimgovernor/issues/3650),
including its service-day and forecast-limit decisions.
**Implementation:** Supported in shared portfolio selection for every nonempty
planned-room role, with connector value through served rooms. Clothing is priced
by its own effects ([W8](colonist-wellbeing.md#w8--clothing-and-care-facility-value)).
Which bench hosts a recipe no standing bench lists is compared in the same
selection, by the host's site, room temperature, power and own build, and the
adopted host is bound as planned, not re-selected
([H8](#h8--host-alternatives-compared-by-site-and-infrastructure)).

Butchering, cooking and workshop loads are all hosted this way (RG-HABITAT-005).
No preselection of a definition and no per-kind population cap remain. Only a
research-available bench or recipe is a Build alternative. Each alternative is
priced by its build, room effects, power and conduits, and by the walk to storage
for every run. A spot-now/table-later chain is compared as one alternative: the
bridge serves from its own readiness until the replacement serves, and every host
of a recipe shares one demand. Binding builds the bridge first and the replacement
only once the bridge stands. A standing spare is kept beside its replacement or
removed, and that choice is compared like any other (RG-HABITAT-007: an upgrade is
built only when it is worth its labor).

A replaced host is removed by one rule, in the owner's step and in the room
reconcile alike (RG-HABITAT-010). It waits, named, until the replacement serves,
and then while a pawn works one of its bills (`host_retire_held`). The removal
replaces the automatic campfire retirement.

Party, marriage and ritual sites are layout alternatives of the same comparator
(RG-HABITAT-005). The choice is a spot in an existing room, a new site room, or no
site. Each is priced by attendance, the gathering memories, ritual surroundings
and competing uses, and a ritual a planned worship room already prices is not
counted again.

([H10](#h10--spot-bridges-retirement-and-activity-sites-compared-as-layout-alternatives)).
Milestone 8 compares retaining, relocating, storing and removing each standing building with its transition cost, binds the choice on the plan and executes it only after the replacement is observed serving ([H15](#h15--executable-redevelopment-and-retirement-milestone-8)). Evidence H7, H8, H10 and H15 prove the policy path; they do not establish sustained native colony performance.

## RG-HABITAT-004 — Provide suitable places for treatment and recovery

The governor should compare care locations with medical needs: existing beds, a
bed made medical for the occasion, an improvised sleeping spot and an equipped
hospital. A bed serving care should not also count as free sleeping or treatment
capacity, and a separate hospital is not a prerequisite for an operation.

**Intent:** Documented in [medical care](../developers/contracts/medical-care.md#care-capacity),
[support work](../developers/contracts/support-work.md#care-census),
[spatial](../developers/contracts/spatial-contracts.md#hospital-bed-alternatives) and
[population](../developers/contracts/population-contracts.md) contracts.
**Implementation:** Supported as executed policy and boundary checks, not as a native
result. Each care patient's alternatives (reuse a ready medical bed, tend in place,
convert its own or another bed, a field sleeping spot, a self-tend before a later
walk, or defer) are priced from native facts: the bed's legality, occupants and
reachability, the tend's quality and infection weight, delay against the time the
patient bleeds out, the hypothermia or heatstroke each place adds, healing, and
where its queued operations are done at each bed's actual success factor. A
conversion that leaves another sleeper without a slot is excluded. The chosen
move is an exact typed order (`Rescue` to the bed, or `BedRest`), the bed is held
for its patient until the patient is seen in it, and the hold is re-derived from the
journal. New capacity is a layout alternative. No ideal bed, hospital room label or
conversion-surplus gate remains. Gaps are listed under
[H11](#h11--care-locations-compared-by-readiness-movement-delay-and-outcome).
Evidence H11.

## Evidence and inspection coverage

- **H1 — Housing work.** [ShelterReview](../../brain/src/RimGovernor.Brain.Runtime/Review/ShelterFamily.cs)
  reads housing and room temperatures, then routes each ready need to sleeping,
  temperature or safe-area work. Building work is admitted under the need with
  its claims. [FirstBedroomCloses](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Layout/FirstBedroomCloses.cs)
  follows this through the served governor to native room and bed reads.
- **H2 — Completed first room.** Registered Nightly `layout/first-bedroom-closes`
  starts three colonists with 600 wood and checks that no proper roofed room
  already exists. Its assertions require a planned enclosed, roofed sleeping room,
  bed ownership for all three, the game's Bedroom/Barracks classification, and
  stable siting after construction starts. It also checks for the specific
  bedroom-space deadlock. Thermal safety and a full night's sleep are outside
  those assertions.
- **H3 — Fuel and useful power.** [PowerMethodTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PowerMethodTests.cs)
  asserts that an empty producer with adequate capacity is `Restore` (wire `restoring_producers`) only while the
  restoration choice restores it ([H12](#h12--servicing-and-power-restoration-compared-as-measured-work)). Registered
  OnDemand [power/fuel](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Power/PowerFuel.cs)
  stages a generator, stove and wood; it reads an empty generator and unpowered
  stove, then waits for fuel and powered service. The fixture enables hauling;
  autonomous staffing and fuel acquisition are outside its claim.

### H4 — Cold shelter and useful temperature

ShelterReview routes sleeping and temperature work through shared building work;
the room plan is reconciled against current buildings and roof cells.
[layout/first-room-ice](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Layout/FirstRoomIce.cs)
starts three colonists at a pinned cold temperature, injects an unbuilt plan and
requires a closed sleeping room before serious hypothermia. It checks for a
coupled construction selection as well as the native room.
[production/coupled-thermal](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionCoupledThermal.cs)
requires a leaky planned bedroom's roof to finish, then an occupant to sleep in a
measurably warmer powered room. The case injects tiredness after closure.
[temperature/building](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Temperature/BuildingTemperature.cs)
checks temperature-setting readback, refusal of an invalid setting and replay.
These cover prepared cold and thermal conditions, with climate-wide performance
still an evidence limit. Food F12 separately covers completed cooling.

### H5 — Maintained colony space

[HomeUpkeepFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HomeUpkeepFamilyTests.cs)
and [UpkeepStructureTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/UpkeepStructureTests.cs)
cover upkeep decisions and structural observation encoding respectively. Registered
[upkeep/home-coverage](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Upkeep/HomeCoverage.cs)
runs the governor against connected rooms, observes native Home coverage and
requires restoration of a deliberately removed corridor cell. Home coverage
enables native services; completed cleaning and repair have their own outcomes.
The spatial contract's room reconciliation covers existing rooms as well as new
construction. Growth and destroyed structures change the next required work.

Ordinary repairs and door-to-wall replacement were held here (tracked in
[#4006](https://github.com/davidarcher/rimgovernor/issues/4006) and
[#4007](https://github.com/davidarcher/rimgovernor/issues/4007)); milestone 5 replaced
the holds with a compared choice ([H12](#h12--servicing-and-power-restoration-compared-as-measured-work)).
The Home-area restoration case proves neither action.

### H6 — Growth and changed plans

[ColonyLayoutReplan](../../brain/src/RimGovernor.Brain.Runtime/Colony/ColonyReview.ColonyLayoutReplan.cs)
builds revised room advice from population, growth and current observations, then
adopts it only for the same loaded world and saved plan without a tick rewind.
Rooms already under construction or in use constrain adoption.
[LayoutInputsChangedTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/LayoutInputsChangedTests.cs)
checks population/input drift and distinguishes structural changes from transient
stock or mood changes. [LayoutReplanOffPathTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/LayoutReplanOffPathTests.cs)
checks the asynchronous replan boundary. These connect later colony needs to
replanning; H2/H4 supply bounded native room outcomes, with sustained expansion
and repeated destruction still outside their assertions.

Food preservation connects to [food](food-security.md); repairs and disruption
connect to [protection](protection-and-recovery.md). The inspected cases cover
specific layouts and climate conditions. Large-colony layout quality and repeated
loss recovery require broader evidence. Reuse the existing room and temperature
cases before adding tests; see [T7](review-queue.md#t7--finish-shelter-and-essential-care).

### H7 — Useful room choice, completion and sharing

[PlannedRoomCredits](../../brain/src/RimGovernor.Brain.Policy/PlannedRoomCredit.cs)
connects each room's mechanics to shared investment value from its service day.
[PlannedRoomCreditTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PlannedRoomCreditTests.cs)
checks every nonempty `PlannedRole`, ringed and ringless rooms, and the actual
ledger effect of connector work. Detaching a connector removes its claim to the
served room's value. [RoomSharingCreditTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/RoomSharingCreditTests.cs)
checks sharing through changed value; the static compatibility table is removed.

[CoordinatedConstructionTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/CoordinatedConstructionTests.cs),
[PlannedRoomActivitiesTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PlannedRoomActivitiesTests.cs)
and [PlannedShelterTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PlannedShelterTests.cs)
cover selection, binding and orders for priced room prerequisites, floors,
removals, supply and ongoing native construction. A bare removal without useful
room value no longer wins a ledger tie. Physical roof/site completion remains
available to its real consumers; development-count scoring and telemetry are deleted.

The integrated full gate at `d0fe7897d` passed 3,877 domain tests and 29 workflow
tests, with zero skipped, native builds and contract probes. These are executed
policy and boundary checks, not a new native room-building acceptance result.
The [portfolio contract](../developers/architecture/portfolio-selection.md) owns
selection details; [forecast value](../developers/architecture/forecast-value.md)
owns individual room effects. [K6](colony-commitments.md#k6--care-learning-and-prison-room-consequences)
and [Q6](review-queue.md#q6--approved-forecast-limits-and-separate-work) describe important model limits.

### H8 — Host alternatives compared by site and infrastructure

The station graph no longer preselects the bench that makes a recipe no standing
bench lists. [WorkshopHostAlternativeTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/WorkshopHostAlternativeTests.cs)
check the bounded alternatives (retain the held host, build another catalog
bench, defer), the named truncation past the bound, the baseline keeping ties, a
room's temperature, a generator or conduit site and the new bench's build changing
which definition wins, a network's surplus deciding between an electric and a
plain bench, benches on one network sharing its surplus once each, and an
unconnected bench priced as a known zero while an unread surplus is named and
excludes only its alternative (`regroup_unassessed`).
[FirstRoomPlanTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FirstRoomPlanTests.cs)
carry the first plan through the compared hosts.
[PlannedRoomPredictionTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PlannedRoomPredictionTests.cs)
check that every `BenchPower` state (unneeded, connected, unconnected) survives the
required-facts boundary, and [LayoutCandidateIntegrationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/LayoutCandidateIntegrationTests.cs)
that two trials of one room keep their own candidate-qualified power answers.

Binding is checked through admission:
[PlannedRoomGateTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PlannedRoomGateTests.cs)
and [PlannedRoomSelectionTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PlannedRoomSelectionTests.cs)
cover building the selected piece in its selected stuff, an overlapping Method
refused as a claim Conflict, stale or foreign selections binding nothing, and
Retained and Deferred loads dispatching no bench.
[ActivityContinuationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ActivityContinuationTests.cs)
show construction closing at its placement receipt, an unconfirmed receipt kept,
while the served Need stays unmet, and
[ServiceInvestmentTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ServiceInvestmentTests.cs)
check that a selected host serves each owning Need's own `need_satisfied`
predicate, retired only on fresh facts.

These are executed policy and boundary checks through the full gate: `2826a5578`
passed 4,340 domain tests, `4e8c5297e` 4,345 and `fa06bfd1f` 4,354, each with 30
workflow tests. No native case was executed. The native `BenchPower` read and the
catalog's `RequiresPower` are compiled but not game-verified, nor checked against
live defs; a host piece with no selected stuff still falls back to bulk stuff
choice. The legacy workshop ladder that remained for a Need with no layout plan or
no owned unhosted load was deleted in milestone 3 ([H10](#h10--spot-bridges-retirement-and-activity-sites-compared-as-layout-alternatives)). The [spatial](../developers/contracts/spatial-contracts.md#layout-planner),
[forecast](../developers/contracts/forecast-contracts.md#workshop-and-laboratory-bench-credits)
and [essential-service](../developers/contracts/essential-services.md#hosted-workshop-loads)
contracts own the behavior.

### H9 — Hosted occupants, bed sharing and herd shelter

Milestone 2 sizes sleeping demand and accommodation by occupant, not by colonist
count. [GuestAccommodationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/GuestAccommodationTests.cs)
check that a hosted guest with no vacant bed stays a sleeping target, is built a bed
and assigned it, meets the Need only by observed use of an owned suitable bed, that
hosted guests and slaves size the sleep nodes but not the colonists' demand, and that
no bedroom step postpones a guest's or slave's bed build.
[HabitatOccupantTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HabitatOccupantTests.cs)
check the compatible reachable slot (no crib, medical or prison bed, slave setting
matching, reach), babies as one crib each, accepted hosting quests' lodgers as dated
expected arrivals (formerly known arrivals; [H13](#h13--fields-herds-prospective-capacity-and-utilities-compared-as-priced-consequences)),
the hospitality gate counting vacant compatible slots, and the jail never
marking an owned bed.
[PrisonCreditTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PrisonCreditTests.cs)
check that a loyal or non-recruited prisoner is still credited its accommodation apart from
recruitment, and [PrisonersFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PrisonersFamilyTests.cs)
that `prisoners/recruit` stays unmet until a prisoner owns a bed.
[HerdShelterTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HerdShelterTests.cs)
check the open pen, barn and animal-spot alternatives: exposure deaths avoided decide
the barn, a comfort range covering the climate is a known zero, an unread fact leaves
only the barn unassessed, a barn without a pen is excluded, and the planned barn earns
only through a link to its herd's pen.
[BedShareAlternativeTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/BedShareAlternativeTests.cs)
check the bed-sharing deviations (private, whole-pool and held barracks) against the
baseline and that a worthwhile barracks can win.
[RoomImprovementTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/RoomImprovementTests.cs)
check that a piece is built only when its owners' stage gain beats its cost, a piece
that leaves the stage gains nothing, and an unread fact leaves it unplanned.
[SleepingStandInTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/SleepingStandInTests.cs)
check stand-ins for hosted sleepers and that a stand-in leaves the bedroom owed.

These are executed policy checks through the full gate (4,383 domain tests and 30 workflow tests, contract orphans none). The native case
`bed/built-for-guest` (OnDemand) is registered and compiles but was not executed, nor
was the `StageFixture` guest staging; no native evidence exists for this milestone.
Known gaps, stated rather than priced: a prisoner outside a cell is not fed by wardens and its hunger
is not priced (`prisoner_unfed_outside_cell_not_modelled`); the room-quality minimums for bed replacement, beauty floors,
sculptures and common rooms stay fixed and dining and recreation mood is unpriced; and
the barn is planned and credited only where an animal entry exists, and is not executed (owner step, animal flap at
the pen link, spot assignment and heater remain the deferred gap of
[H13](#h13--fields-herds-prospective-capacity-and-utilities-compared-as-priced-consequences)). Milestone 6 has since
read the pregnancy fact and sized the nursery with hosted babies.
The [population](../developers/contracts/population-contracts.md#accommodation-of-hosted-people),
[spatial](../developers/contracts/spatial-contracts.md#layout-planner),
[husbandry](../developers/contracts/husbandry-contracts.md#herd-shelter-alternatives)
and [forecast](../developers/contracts/forecast-contracts.md#prison-accommodation-credit)
contracts own the behavior.

### H10 — Spot bridges, retirement and activity sites compared as layout alternatives

Milestone 3 removes every preselected production host, and the evidence below covers
that whole path.

- **Host alternatives.** [WorkshopHostAlternativeTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/WorkshopHostAlternativeTests.cs)
  check butcher, cooking and workshop hosts as complete alternatives:
  - distance to storage and the haul it prices;
  - kitchen food poisoning by cleanliness, and unread cleanliness unassessing only its room;
  - an unconnected bench's conduit cost;
  - the butcher spot against the table by consequence, and no population cap;
  - only research-available benches and recipes offered as Build alternatives;
  - chains against spot-only and table-only, with bridge readiness;
  - one shared demand across hosts, and a pair that wins only past one host's day;
  - keeping or removing a spare.
- **Retirement.** [HostRetirementTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HostRetirementTests.cs)
  check that a spare is retired only once its replacement serves. A placed, unbuilt,
  unpowered or bill-less replacement serves nothing. A bill in progress holds the
  removal, a claimed heating campfire is never taken, and a kept spare takes copies of
  orders. The room reconcile never cuts a bridge spot while its bill is worked or before
  its replacement serves.
- **Binding through Hands.** [PlannedRoomGateTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PlannedRoomGateTests.cs)
  (`ABridgeSpotIsPlacedFirstThenItsBenchAndRetiredOnlyOnceTheBenchServesAndNoBillIsWorked`)
  runs one Need's whole planned-host step through admission and the Hands receipts:
  1. the spot is placed first and its receipt closes it;
  2. a sited spot binds nothing more;
  3. the built spot lets the bench be placed;
  4. once the comparison retains the bench alone, the spot is not removed while the bench does not serve;
  5. while a pawn works the spot's bill, the removal is held as `host_retire_held`, naming the bill;
  6. once no bill is worked, the removal is admitted as the spot's `host-retire-*` deconstruction Method.
- **Activity sites.** [ActivitySiteTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ActivitySiteTests.cs)
  check each kind's demand from facts, with no ritual without Ideology. They check that
  each eligible room, a new room and no site are offered up to the room bound, that a
  standing or in-flight site is never deviated from, and that each deviation is one
  alternative of the shared regroup batch. The consequence decides the site, the baseline
  keeps a tie, and a new room earns attendance an existing room without capacity cannot.
  The adopted sites survive the store. Placement is one Method per kind and room, whose
  room is then read spent, a site has no retire or upgrade lifecycle, and completion and
  provision are distinct in both directions.
  [RoomsFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/RoomsFamilyTests.cs)
  chain hosting, the site Needs and gatherings by precondition.

These are executed policy checks through the full gate. The domain counts were 4,388
at `144311922`, 4,391 at `a500fe7b3`, 4,402 at `98ea8a70d`, 4,403 at `70d0bd767` and
4,419 at `af8e5a35d`, and 4,418 with this evidence, each with 30 workflow tests and
contract orphans none.

The OnDemand native case `production/spot-bridge`
([ProductionSpotBridge](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Production/ProductionSpotBridge.cs))
stages four fresh muffalo corpses, no butcher bench and wood in stock. The corpses come
from the new `StagePawn.Dead` staging. The case asserts three things:

- the comparison adopts a `ButcherSpot`-to-`TableButcher` chain, and the spot is built and billed;
- no table is placed before the spot stands built;
- the spot is removed only once a built table serves with a butcher bill, and a spot bill
  worked after that is held as `host_retire_held`.

The case is registered and compiles but was not executed, and neither was the `Dead`
staging. No native evidence exists for this milestone.

Known gaps, stated rather than priced:

- builder skill, ingredient search radius, ingredient value and spoilage are unpriced;
- generators are not offered as a host alternative;
- kinded orders bypass the comparator except through upgrade chains;
- `SelectPaste` is kept;
- ideology-gated recipes are not filtered;
- party and marriage surroundings carry no price beyond the roof;
- `WorkRules.ReconcileLedger`'s orphan-bill removal after three reviews can still
  interrupt a worked bill (pre-existing);
- no live-game run has read `StorageReach`, `BillState.Workers`,
  `BillStackRow.Reachable` or `ActivityAccess`.

The [spatial](../developers/contracts/spatial-contracts.md#host-chains-and-pairs),
[forecast](../developers/contracts/forecast-contracts.md#workshop-and-laboratory-bench-credits),
[essential-service](../developers/contracts/essential-services.md#hosted-workshop-loads),
[mood](../developers/contracts/mood-control.md#activity-sites) and
[method-continuation](../developers/contracts/method-continuation.md#workshop-host-construction)
contracts own the behavior.


### H11 — Care locations compared by readiness, movement, delay and outcome

Milestone 4 replaces the ideal-bed, hospital-label and conversion-surplus logic with a
comparison of where each patient is cared in
([medical care](../developers/contracts/medical-care.md#care-capacity)).

- **Census and legality.** [SupportMethodTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/SupportMethodTests.cs)
  check that a bed tend is the care capacity choice and never a coupled alternative, and
  that a surgery with no bed to be operated in is not credited.
  [HabitatOccupantTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HabitatOccupantTests.cs)
  check that each occupant kind is legal only in its own bed class
  (`SleepingRules.LegalBed`) and that a patient held in a medical bed is no free slot and
  wants no second one.
- **Capacity.** [CareCapacityTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/CareCapacityTests.cs)
  check converting the patient's own bed against waiting for a new one and a ready ward,
  another occupant's bed converted only when they keep a slot, a partner sharing the bed
  protected, a post-care restore only when its sleeper needs the bed, a refused conversion
  chosen without at the next review, a bed the census does not describe left unassessed by
  name, the goal floor converting a planned medical bed first, and new medical beds as layout
  alternatives the shared comparator decides.
- **Transport and holds.** [CareTransportTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/CareTransportTests.cs)
  check a downed patient carried and a walking colonist laying down in the exact bed, a
  walking guest, prisoner or animal excluded naming its state, a held bed free for no second
  patient or sleeper, a refused transport excluding its bed and crediting nothing, a hold
  released only for the patient the census no longer cares for, an open transport claiming its
  bed against every other order, and payloads that require their exact bed.
- **Field care.** [CareFieldTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/CareFieldTests.cs)
  check a field spot beating a carry the bleeding does not survive, exposure priced over a
  common horizon, an unread temperature leaving its alternative unassessed, a placed spot
  made medical then carried to and a refused placement excluded, a stabilizing self-tend
  moving nobody until the census reads the patient recovering, and a recovering patient
  walked from its spot with no bed converted.
- **Surgery location.** [SurgeryLocationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/SurgeryLocationTests.cs)
  check an operation proceeding in a non-medical bed when none gives a better chance, a hospital
  transfer winning only when its chance gap outweighs the move, a prisoner's operation compared
  over its prison-legal beds, a vitals monitor raising the chance through the bed's stat, and an
  unread bed factor leaving an alternative unassessed naming the fact.
  [SurgerySelectionTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/SurgerySelectionTests.cs),
  [SkillHealthGoalTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/SkillHealthGoalTests.cs)
  and [HospitalCreditTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HospitalCreditTests.cs)
  were rewritten: only a missing care bed makes an operation bed short
  (`SurgeryChance.NoCareBed` replaces the ideal-bed heuristic), a health goal is blocked
  `no_care_bed` only when no operation has a bed, electives depend on the rated chance alone,
  and the hospital credit trades the patient's existing medical bed's thoughts and gives a bed as
  good as the hospital's no surgery credit.

These are executed policy and boundary checks through the full gate (4,454 domain
tests and 30 workflow tests at the last milestone commit, contract orphans none). The
OnDemand native case `medical/field-treatment`
([FieldTreatment](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Medical/FieldTreatment.cs))
stages a downed colonist cut and bleeding about 30 cells from the only bed with a
doctor beside it, and passes when the bleeding stops or every cut is tended and the
patient is alive. It is registered and compiles but was not executed, nor was the
`StagePawn.Cut` staging. The nightly `custody/population` case now names a ward bed for
its rescue and checks the no-bed and guest lay-down refusals; it changed and was not
run. No live-game run has read the new native facts (`CareSurgeryFactor`, bleed and
temperature, `BedRest`, exact `Rescue`), so no native evidence exists for this milestone.

Known gaps, stated rather than priced:

- a failure's price is a heal-days proxy (damage over the bed's healing a day), not
  `HarmConsequences.Wounds`;
- the surgeon's approach to the operating bed is read to the patient's current cell,
  and transit temperature uses the current cell;
- a planned bed's `MedicalTendQualityOffset` is unpriced;
- a recovering patient's carried transfer has no issuer (walks only);
- a deleted field spot cannot be placed again under the same native authority, and a
  leftover medical spot is restored only by the post-care pass;
- the rescuer a rescue names (`RescuerFor`) may differ from the one priced;
- a downed patient outside the care census is left to native rescue;
- `MedicalPawns` and `CarePawn` are not widened: prisoner operations stay on
  `OrganHarvest` and animals on `HerdWard`;
- `hospital_guest_surgery_unmodelled` stays, because a guest's operation in a colony
  hospital bed is not credited.

Placement is tracked apart from use throughout: a placed spot, an accepted order or a
claimed bed establishes neither treatment nor access. A patient is credited only when a
later census sees it lying in the bed, which keeps RG-HABITAT-011's separation of
provision from outcome for care. The [medical](../developers/contracts/medical-care.md#care-capacity),
[support-work](../developers/contracts/support-work.md#care-census),
[population](../developers/contracts/population-contracts.md),
[spatial](../developers/contracts/spatial-contracts.md#hospital-bed-alternatives),
[persistence](../developers/contracts/persistence-contracts.md) (schema 73),
[action](../developers/contracts/action-contracts.md) and
[method-continuation](../developers/contracts/method-continuation.md#care-chains) contracts own the behavior.


### H12 — Servicing and power restoration compared as measured work

Milestone 5 (RG-HABITAT-002, -008, -010, -011) replaces the repair and wall-replacement
holds, the fuel and repair waits and the disaster recovery selection with one comparison of
measured servicing work. The [forecast](../developers/contracts/forecast-contracts.md#servicing-activity-read),
[upkeep](../developers/contracts/upkeep-contracts.md#repairs-cleaning),
[power](../developers/contracts/power-contracts.md#restoration) and
[persistence](../developers/contracts/persistence-contracts.md) (schema 74) contracts own the behavior.

- **Measurement (RG-HABITAT-010).** [ServicingWorkTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ServicingWorkTests.cs)
  check that a repair, breakdown fix, refuel and door-to-wall replacement take the native drivers' ticks at the pawn's
  measured rate (repair warm-up and per-hit-point work, fix ticks divided by the success chance with the component
  consumed on failure, refuel in loads of the carry capacity, the replacement's deconstruct, delivery and build with the
  leavings recovered and the cell left open), that a target joins its canonical row by id and a mismatched def or cell is
  a contract violation, that each missing fact is named and never defaulted, and that a known refusal excludes an option
  with its reason.
- **Maintenance choice (RG-HABITAT-010, -002).** [HomeUpkeepFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HomeUpkeepFamilyTests.cs)
  check that a damaged wall gets a claimed repair and stays deficient until fresh facts show it whole, that a door the
  clearance step walls is replaced when repair cannot restore its service and repaired otherwise, that a builder whose
  work types are inactive is excluded `work_inactive` while repair and fix (forced ordered jobs) are not, that Defer wins
  only when every work option is excluded and keeps the Need visible with the reasons, that Retire wins only when its
  loss is assessed zero (a clearance target) and is otherwise unassessed naming the service, and that a refused receipt
  spends the budget and the next review reselects. The `unsupported_forecast_repair` and
  `unsupported_forecast_wall_replacement` holds are gone.
- **Power restoration (RG-HABITAT-002, -008).** [PowerRestorationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PowerRestorationTests.cs)
  check that an empty generator with fuel in stock is refuelled and the Need closes only on fresh powered facts, that
  with no stock and a tree to cut the acquire-then-refuel chain is bound and a later review with stock refuels, that with
  no path the chain is the named state `fuel acquisition:<def>` and generation wins, that a broken generator is fixed
  with a component and otherwise names `component acquisition:<def>`, that two empty generators never claim one stock
  twice, that the native `Props.autoRefuelPercent` gates native starts, and that a refused refuel spends the budget.
  [PowerMethodTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PowerMethodTests.cs) check that a lost producer
  counts toward capacity only while restorable. The `.25` refuel fraction is deleted for the building's own threshold.
- **Disaster services (RG-HABITAT-011).** [DisasterFamilyTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/DisasterFamilyTests.cs)
  check that `disaster/services` holds `services_by_owning_families` (naming power/basic and home-upkeep/repairs) and
  closes only on the disaster history's fresh finding. The selection layer (`SelectRecoveryMethods`, `RecoveryPlanning`,
  the retained `ReviewState.Recovery` and its five codecs) is deleted through every layer; schema 73 to 74 replaces older
  journals that carried it.
- **Native case.** OnDemand `upkeep/restore-service`
  ([RestoreService](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Upkeep/RestoreService.cs), 15,000 ticks, one
  colonist, families power and repair) stages an empty `WoodFiredGenerator` wired to an `ElectricStove` with 150 wood,
  a wooden `Wall` in Home at 100 hit points, no components, and work priorities at zero (forced orders still go; active
  vanilla labor would pre-empt the brain). It asserts that no blueprint or frame appears, that `refuel-<generator>` and
  `repair-<wall>` are selected through the normal path, that each Need stays open while its order is in flight and closes
  only on the observed fuelled and powered generator, powered stove and full-health wall. It is registered and compiles
  only: it is OnDemand, and OnDemand native cases are executed by no automated run (not the normal gate, not
  `RimGovernor.cmd test -full`, which only forces the normal gate, and not the nightly). No run has read the native servicing census, so
  no native evidence exists for this milestone, and no executed pass is claimed.

Known limits, stated rather than priced:

- a damaged producer still produces, so condition repair is not a power deficit and stays `home-upkeep/repairs`';
  the breakdown fix is home-upkeep's Method even for a generator (power only counts it);
- the refuel order is issued even when native auto-refuel would also start it;
- a second producer with only a partial stock left is excluded `fuel_taken` and defers; the fuel count is held by
  the policy ledger, not `MethodClaims`, and the component ledger is count-based (no stack id);
- only the harvest path of fuel acquisition is modelled (chemfuel production and trade are the named state
  `fuel acquisition:<def>`), and no component acquisition path exists (`component acquisition:<def>`);
- a turret's and other non-power buildings' service value is unmodelled (`service:<def>`), and the use-driven fuel
  rate of a per-use comp is named (`fuel use rate:<def>`), not priced; a gun turret's refuel is nonetheless issued by the
  defense step through this servicing choice ([H14](#h14--defense-alternatives-probed-compared-and-bound-as-a-planned-site));
- the supply legs assume the nearest reservable stack serves every load (`servicing_supply_nearest_stack_legs`);
- deconstruction needs a designation, worked by native labor, so a replacement depends on both giver work types
  being active;
- the servicing read lists at most 32 targets; a larger set is cut with `servicing_targets_truncated` and the absent
  targets are unassessed, not unread;
- the private driver constants (1.7 work per speed, 80 and 20 repair work, 1000 fix ticks, 240 refuel ticks, the
  industrial component) are copied from the decompiled drivers.

Provision stays separate from outcome as RG-HABITAT-011 requires: an accepted order, a chosen option or a staged
acquisition establishes nothing, and an unread fact is named rather than defaulted.

### H13 — Fields, herds, prospective capacity and utilities compared as priced consequences

Milestone 6 (RG-HABITAT-006, -007, -008) replaces the field and crop placement ladders, the herd's separately planned
feeding, shelter and containment, the fixed spare-capacity quotas and the generator ranking with comparisons priced in
capability-days. The [spatial](../developers/contracts/spatial-contracts.md#field-placement-comparison-rg-habitat-006),
[husbandry](../developers/contracts/husbandry-contracts.md#herd-arrangement-needs),
[forecast](../developers/contracts/forecast-contracts.md#prospective-capacity),
[power](../developers/contracts/power-contracts.md#comparison),
[population](../developers/contracts/population-contracts.md),
[action](../developers/contracts/action-contracts.md) and
[persistence](../developers/contracts/persistence-contracts.md) (schema 75) contracts own the behavior.

- **Fields and crops (RG-HABITAT-006).** [FieldPlacementTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/FieldPlacementTests.cs)
  check that the comparison refuses without a plan or an owed need, that a more fertile later patch beats the first
  feasible one, that food and textile crops compete for one patch without sharing a cell, that a half roofed, half
  infertile patch is credited only its usable cells (a nominal zone area establishes nothing), that an out-of-season crop is
  excluded by name, that a covered bound zone is retained and an owed one grows in place deterministically, that ground
  a building site holds is never taken, and that an in-flight zone create holds its ground on the ledger until observed and
  a refusal releases it. [LayoutFieldPlanningTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/LayoutFieldPlanningTests.cs)
  carry the ledger through the layout plan.
- **Herd arrangement (RG-HABITAT-006).** [HerdArrangementTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HerdArrangementTests.cs)
  check that a marker or a complete fence over bare ground leaves feeding open (placing a pen marker establishes no
  accommodation), that hay feeds back as runway and feeding closes only when observed nutrition covers consumption,
  that an unread growing season is named and keeps the arrangement open, that a cold species closes shelter only on an
  observed reachable roofed room in its band and a hardy one has no shelter deficit or barn, that an unenclosed pen is a
  containment deficit repaired over the retain baseline by the roaming loss it stops, that adequate summer pasture asks
  no hay field, and that two pens cannot claim the same feed twice.
  [HerdShelterTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HerdShelterTests.cs) check that a barn earns only
  where an animal entry exists.
- **Prospective capacity (RG-HABITAT-007).** [ProspectiveCapacityTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ProspectiveCapacityTests.cs)
  check that a full colony with no expected change builds no spare bed (no quota), that an arrival past the bed's lead
  starts at its lead and one inside it starts now, that two lodgers due in three days prepare their beds at the review the
  lead requires and a cancellation retires them, that an unassessed loss defers naming the fact, that a bed share covers a
  short visit and a bedroom a recruit, that a pregnancy dates the birth, its crib and the mother's bed rest, that a baby
  leaving its crib is priced by its own rest and admitted only once its bed is due, that the season's end dates the hay
  demand and a field is sized only when ready by then, and that a chain's later step raises dated power demand once its
  bridge stands. [HabitatOccupantTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HabitatOccupantTests.cs) and
  [QuestStateTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/QuestStateTests.cs) check the dated arrival and
  departure reads that replace `KnownArrivals`.
- **Utility choice (RG-HABITAT-008).** [PowerUtilityTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PowerUtilityTests.cs)
  check that switching an idle bench off beats a new generator while a freezer spoils, that solar wins in open sky and
  wood in a cave, that roofing an exposed bank beats a second battery only while the rain rate is read, that a passive
  cooler in band beats the powered one when power is short and is filled from the thermal read, that a dated chain draw
  prepares generation with lead and defers when its loss is unassessed, that storage is sized by the priced
  interruption, that routing to a surplus network beats a new generator, and that a short winter network of a bench, a
  patient monitor and a bedroom heater builds. Every consumer's loss is its own valuation (spoilage avoided, recipe run
  value, surgery loss, sleep-temperature mood), not a power-only price.
  [PowerSwitchTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PowerSwitchTests.cs) check that an idle bench is
  switched off, held and switched back on in surplus, that a switch the player set is ordered under Auto unless
  protected, that a non-flickable consumer is never switched, that switches are ordered off first, that an accepted
  switch that still reads unchanged is unconfirmed and not re-issued, and that a refusal spends the budget.
  [PowerRestorationTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PowerRestorationTests.cs) and
  [PowerMethodTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PowerMethodTests.cs) carry the restoration choice
  of H12 into the comparison.
- **Native cases.** OnDemand `upkeep/restore-service` (H12) and OnDemand
  [power/configure-and-route](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Power/PowerConfigureAndRoute.cs)
  (a fuelled wood-fired generator wired to a cooler and three idle electric stoves, and a lamp out of the conduit's
  reach) are registered and compile only; OnDemand native cases are executed by no automated run (not the normal gate, not
  `RimGovernor.cmd test -full` and not the nightly). `power/configure-and-route` expects a `configure-`
  Method's switches to turn an idle stove off, a colonist to flick it, the building read to observe its switch off, the
  cooler to stay powered and a conduit to reach the lamp. Only the game proves the native power gizmo, the flick and the
  switch readback, so no native evidence exists for this milestone and no executed pass is claimed.

These are executed policy and boundary checks through the full gate (4,518 domain tests and 30 workflow tests at the
milestone 6.5 commit, contract orphans none).

Known limits, stated rather than priced:

- the deferred animal-flap gap: a barn shelters animals only if roamers can enter it, and no animal flap at the pen
  link, barn owner step, animal spot assignment or heater is built, so a barn's credit is zero until an animal entry
  exists (`HerdShelterRules.AnimalEntry`);
- the native tick formulas that date a birth, a baby's leaving the crib, an arrival and a departure are not checked
  against live saves, and a quest structure other than a delay-to-leave or delay-to-quest-end reads `Unread` and
  defers; prisoner recruitment and animal pregnancies are not dated;
- an arrival's and a birth's loss beyond a bed's rest is unassessed (the arrival's rest loss is a lower bound with the
  mood term omitted), so only a bed ahead of a dated arrival or a leaving baby can win; a birth's crib and maternal bed
  rest defer naming the fact;
- the prospective bed's lead is optimistic (its work over builders, no material delivery);
- an unvalued consumer (lamps and other consumers, turrets, the immunity gain a patient loses, tend quality) is
  unassessed naming the service, so a priced interruption is a lower bound;
- the rain short-circuit roll (every 97 ticks, 2 %, `0.2 * N * RainRate`, a battery over 500 Wd losing 400 Wd with 5 %
  chance) is copied from the decompiled game ([kept constants](../developers/contracts/kept-constants.md#mirrored-game-literals-rain-short-circuit-habitat-64)),
  and a wind turbine's clear path, breakdown rate, shared dependence on one producer and material delivery time are not
  priced;
- an ordered switch is confirmed only by a later building read, and the native gizmo toggle, flick and readback are
  untested in game;
- the field ledger's fertility, season and roofing reads and the herd's feeding runway are policy checks, not native
  results, and no field or pen was built under the new comparison in a game.

### H14 — Defense alternatives probed, compared and bound as a planned site

Milestone 7 (RG-HABITAT-009, with -003, -008 and -010 where noted) replaces the fixed killbox, the tier order, the
perimeter ring and the turret proposals with generated alternatives, a game-geometry probe of what each protects, a
comparison priced in capability-days, and one chosen alternative bound into the layout plan and worked as a chain. The
[spatial](../developers/contracts/spatial-contracts.md#defense-candidates-rg-habitat-009),
[forecast](../developers/contracts/forecast-contracts.md#raid-harm-and-protection-in-the-defense-comparison),
[action](../developers/contracts/action-contracts.md#defense-chain-methods-habitat-74),
[upkeep](../developers/contracts/upkeep-contracts.md#defensive-layout),
[otel](../developers/contracts/otel-signals.md),
[kept-constants](../developers/contracts/kept-constants.md#mirrored-game-rules-cover-block-chance-habitat-72) and
[persistence](../developers/contracts/persistence-contracts.md) (schema 76) contracts own the behavior.

- **Alternatives (RG-HABITAT-009, -003).** [DefenseCandidatesTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/DefenseCandidatesTests.cs)
  check that with no threat read no killbox is laid and the other alternatives still stand, that ground a field or a
  bedroom holds refuses the candidate naming the holder, that closing the only door excludes a candidate by its route
  while a longer route is priced by the added cells, that Retreat uses the standing interior doors and claims no ground,
  that sandbags need no research and turrets do (`research:<prereq>`), and that the order is by kind and cells, never a
  tier index. [DefenseTierNodesTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/DefenseTierNodesTests.cs) carry the
  killbox tiers the Killbox candidate lays.
- **Probed protection (RG-HABITAT-009, -008).** [DefenseProtectionTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/DefenseProtectionTests.cs)
  check that rows join the candidates by id and an unknown id is refused, that violence-incapable and downed colonists
  are not staffing, that an unpowered turret protects nothing and is named `power:<turret>` while a powered one fires
  its burst, that rifles need no ammunition and a mortar counts its shells, that a field on the approach counts before the
  wall and not after, that a recorded native reply gives the cover and the doored perimeter different protection, that a
  raider's weapon range sets the exposed positions, that the probe round-trips exactly, and that no observed arrival
  edge leaves every term unassessed rather than zero. The probe is bounded at 16 candidates, 8 firing and 24 approach
  cells each and 4096 pieces.
- **Comparison (RG-HABITAT-009, -010).** [DefenseChoiceTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/DefenseChoiceTests.cs)
  check that a sandbag line beats a doored perimeter at the same protection until its sight is fogged, that Retreat wins
  when every firing line needs more shooters than the colony has, that replacing a killbox with a perimeter carries its
  downtime and unread threat timing retains, that removing standing sandbags is charged their protection, that claimed
  builders lengthen the lead and a short power network interrupts a turret, that an accepted build leaves the deficit
  until the piece stands and draws, that ties go to Retain, and that a temporary line wins on a game-shaped reading and
  retains while any of it is unread. An alternative with an unassessed term the baseline has assessed never wins.
- **Binding and the chain (RG-HABITAT-009, -010).** [DefenseBindingTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/DefenseBindingTests.cs)
  check that the chosen defense is a planned site whose ground other planners keep off, that ordinary doors are kept and
  closing an access path is excluded, that a bound choice survives load without reselection, that a destroyed piece
  reopens its build, that losing the replacement midway holds the removal, that a refused build spends the refusal budget
  and that a multi-cell piece's preview is checked over its whole footprint. [DefenseNamedStatesTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/DefenseNamedStatesTests.cs)
  check that a spent turret barrel is refuelled through the servicing choice and an unpowered or empty one is a service
  deficit. [StoreSchemaPinTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/StoreSchemaPinTests.cs) pin schema 76.
- **Native case.** OnDemand `defense/temporary-cover`
  ([TemporaryCover](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Defense/TemporaryCover.cs), 30,000 ticks: a walled
  hut with one door, a colonist with an assault rifle, no standing defense and one hostile gunner walking in from the map
  edge) expects the comparison to bind the sandbag TemporaryCover and work a `defense-choice-temporary_cover-` Method,
  every blueprint and frame to be sandbags, the Need to stay open while they are only ordered and to close only on the
  observed built sandbags. It is registered and compiles only. OnDemand native cases are executed by no automated run:
  not the normal gate, not `RimGovernor.cmd test -full` (which only forces the normal gate) and not the nightly. Only the
  game proves the native raid track and its weapon range, the probe of standing cover and the closing facts, so no
  native evidence exists for this milestone and no executed pass is claimed.

These are executed policy and boundary checks through the full gate (4,542 domain tests and 30 workflow tests at the milestone 7.4 commit, contract orphans none).

Known limits, stated rather than priced:

- the chain does not claim builders per pawn (`BuildingAction` carries no pawn); free builders are netted only through the
  work view, and the lead is optimistic accordingly;
- the approximations of the comparison: a power shortfall scales the whole coverage instead of the turrets it starves,
  and removing a defense charges its full baseline protection;
- `Covered` is a probed-cell fraction, a stated stand-in for the share of the loss the defense stops, not a simulated
  fight; the approach cells are the newest three observed ground raids' walks sampled every third cell (a policy choice),
  so a colony that has seen no ground raid since load has every term unassessed (`arrival_edges:none_observed_since_load`)
  and no term is compared as a number;
- the cover block chance restates the private `CoverUtility.TryFindAdjustedCoverInCell` factors
  ([kept constants](../developers/contracts/kept-constants.md#mirrored-game-rules-cover-block-chance-habitat-72));
- raider weapon range comes from the native raid track and is memory, not saved: it is read live only, and the native
  read has not been run in a game;
- a killbox's mining is unpriced (`dig:mining_unpriced`), a turret or other unvalued consumer's loss is a lower bound,
  and an unpriced piece is `piece:<def>`;
- `RetainPerimeter`, `PerimeterHold` and the stored `DefenseLayoutRecord` are still read for a layout the comparison
  retains; a stored layout's retier, turret power hold and cover clearance are the older tier-by-tier code;
- the planned perimeter and killbox sites are still traced with `PlanPerimeter`, and the routing cut stays advice that is
  logged only.

### H15 — Executable redevelopment and retirement (milestone 8)

Milestone 8 (RG-HABITAT-010 and -011, with -001, -002 and -003 where noted) replaces omission-driven removal of standing
buildings with a comparison of retaining, repairing, replacing, relocating, repurposing, storing and removing each one,
binds the chosen transition on the layout plan, admits a removal only while what the building provides is observed served
by a replacement, and executes a chosen relocation as one native reinstall. The
[spatial](../developers/contracts/spatial-contracts.md#standing-building-transitions-rg-habitat-010-and-011),
[forecast](../developers/contracts/forecast-contracts.md),
[action](../developers/contracts/action-contracts.md#standing-building-transitions-habitat-84),
[husbandry](../developers/contracts/husbandry-contracts.md),
[otel](../developers/contracts/otel-signals.md),
[kept-constants](../developers/contracts/kept-constants.md#mirrored-game-literals-transitions-habitat-81-84) and
[persistence](../developers/contracts/persistence-contracts.md) (schemas 77 and 78) contracts own the behavior.

- **Comparison with transition cost (RG-HABITAT-010).** [TransitionChoiceTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/TransitionChoiceTests.cs)
  check that a damaged idle bench is retained and only a breakdown under a bill queue buys the fix
  (`ADamagedIdleBenchIsRetainedAndOnlyABreakdownUnderABillQueueBuysTheFix`), that a building the native cannot minify is never
  packed and a minifiable bed relocates (`ABuildingThatCannotBeMinifiedIsNeverPackedAndAMinifiableBedRelocates`,
  `TheNativeMinifiableReadNotACatalogSetDecidesPacking`), that Store is excluded with no storage that accepts the building
  (`StoreIsExcludedWithNoStockpileThatAcceptsTheBuilding`), that Remove credits the recovered fraction, charges the service and
  loses to Retain while the service is unassessed
  (`RemoveCreditsTheRecoveredFractionChargesTheServiceAndLosesToRetainWhileTheServiceIsUnassessed`), that a spare bed loses its
  removal while a dated arrival needs it (`ASpareBedLosesItsRemovalWhileADatedArrivalNeedsIt`) and that a host retirement is a
  compared Remove that loses while the replacement does not serve (`AHostRetirementIsAComparedRemoveThatLosesWhileTheReplacementDoesNotServe`).
  [TransitionServiceTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/TransitionServiceTests.cs) check the proposing
  path: an unowned bed on a planned kitchen footprint is stored and an owned one stays
  (`AnUnownedBedOnAPlannedKitchenFootprintIsStoredAndAnOwnedBedStays`), omission alone never removes and a gated removal is a
  named hold with no order (`OmissionAloneNeverRemovesAndAGatedRemovalIsANamedHoldWithNoOrder`), a beautiful sculpture in an
  owned bedroom stays on merit (`ABeautifulSculptureInAnOwnedBedroomStaysAndAPlainOneTheFurnishingOmitsStaysOnMerit`), a stale
  piece on a listed piece's slot cannot stay and its read service prices its removal
  (`AStalePieceOnAListedPiecesSlotCannotStayAndItsReadServicePricesItsRemoval`), and a minifiable bed relocates into an open bed
  slot rather than the slot being built new (`AMinifiableBedRelocatesIntoAnOpenBedSlotRatherThanTheSlotBeingBuiltNew`).
- **Binding and stale execution (RG-HABITAT-010, -011).** [TransitionBindingTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/TransitionBindingTests.cs)
  check that bound transitions survive a load and are not compared again (`BoundTransitionsSurviveLoadAndAreNotComparedAgain`),
  that a rewind to before planning leaves none, that a receipt for an absent transition is rejected and closes nothing, that a
  replacement that stops serving reopens the dependency, that a filled slot re-selects a relocation, that an older plan blob
  restores as no plan and the current one loads (`AnOlderPlanBlobRestoresAsNoPlanAndTheCurrentOneLoads`) and that no stored
  type but the layout plan carries a transition (`NoStoredTypeButTheLayoutPlanCarriesATransition`, no second ledger).
  [TransitionStaleWorkTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/TransitionStaleWorkTests.cs) check that an
  unissued removal the plan no longer carries is cancelled and not issued again in the same load on each of the four paths:
  host retirement (`AStaleHostRetirementIsCancelledAndNotIssuedAgain`), room reconcile
  (`AStaleRoomRemovalIsCancelledAndMethodOnceHoldsIt`), bed removal (`AStaleBedRemovalIsCancelledAndNotIssuedAgain`) and
  clearance (`AStaleClearanceRemovalIsCancelledAndWithdrawnFromTheNextStep`).
- **Observed replacement before removal (RG-HABITAT-010, -011, -001, -002).** [TransitionBridgingTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/TransitionBridgingTests.cs)
  check that an occupied bed is kept while its replacement is unassignable and removed once the sleeper is assigned
  (`AnOccupiedBedIsKeptWhileItsReplacementIsUnassignableAndRemovedOnceTheSleeperIsAssigned`), that a generator is held until its
  replacement powers the net and a replacement breakdown reopens the dependency and cancels the unissued removal
  (`AGeneratorIsHeldUntilItsReplacementPowersTheNetAndAReplacementBreakdownReopensAndCancels`), that an old fence is held while
  the new pen has a gap and goes once it is closed and fed
  (`AnOldFenceIsHeldWhileTheNewPenHasAGapAndRemovedOnceItIsClosedAndFed`), that a retired turret is held until the planned
  defense pieces stand and a lost piece reopens it
  (`ARetiredTurretIsHeldUntilTheDefenseReplacingItStandsAndALostPieceReopens`), and that a settled transition's open removal is
  cancelled on the first review after a load (`ASettledTransitionsOpenRemovalIsCancelledOnTheFirstReviewAfterLoad`).
  [HerdArrangementTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HerdArrangementTests.cs) check that a pen piece is
  held only on the herd whose pen it bounds (`APenPieceIsHeldOnlyOnTheHerdWhosePenItBounds`).
- **Native-safe execution (RG-HABITAT-010, -003).** [TransitionHandsTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/TransitionHandsTests.cs)
  check that a chosen Relocate is one `MoveBuilding` of the standing building onto its slot's anchor and rotation under the
  `<room>-reinstall-<target>-<x>-<z>` Method (`TheReinstallIsOneMoveOfTheStandingBuildingOntoItsSlot`), that a Refused order
  of each kind (Remove, Store, Relocate) ends the attempt, is held by `MethodOnce` and never settles the transition while the
  census does (`ARefusedOrderIsConsumedNotReissuedAndNeverSettlesTheTransition`), that a reinstall whose slot another piece took
  is cancelled and not issued again (`AReinstallWhoseSlotIsTakenMidChainIsCancelledAndNotIssuedAgain`), and that a reinstall onto
  another room's slot waits only on work over the slot (`AReinstallOntoAnotherRoomsSlotWaitsOnlyOnWorkOverTheSlot`).
  [StoreSchemaPinTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/StoreSchemaPinTests.cs) pin schema 78, which deleted
  the plan's save shims.
- **Native case.** OnDemand `redevelopment/replace-before-remove`
  ([ReplaceBeforeRemove](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Redevelopment/ReplaceBeforeRemove.cs): four fresh
  muffalo corpses, no butcher bench, food short and wood in stock, under the served bill and work families) expects the
  comparison to adopt a butcher-spot-now, table-later chain, the plan to bind the spot's `host_retirement` transition with its
  replacement dependency, the table to be built and observed serving with a butcher bill, only then the spot to be
  deconstructed (no Deconstruct designation stands on it while the table is not serving), and the plan to drop the transition
  once the spot is gone. It is registered and compiled only, and executed by no automated run: OnDemand cases are not in the
  normal gate, `RimGovernor.cmd test -full` or the nightly. No native evidence exists for this milestone and no executed pass is
  claimed. Its own limit: it polls once a round, so a run that never observes the binding before the spot is gone fails the
  case rather than passing it.

These are executed policy and boundary checks through the full gate, except the native case above.

Known limits, stated rather than priced:

- Repurpose has no typed operation (`no_typed_op`), so it is always excluded and no building is repurposed;
- the defense bridging read checks that the planned tiers' pieces stand, not observed coverage (`DefenseVerified` is runtime
  memory), so a standing piece that does not protect is not told from one that does;
- power conduits and structure are not transition targets and none is proposed for removal, and definitions other lanes carry
  in owned rooms are never proposed;
- Relocate exists only on planned ground: clearance, a host's spare and a spare bed have no slot (`no_destination`), and the
  haul to a slot is a lower bound because no path to a cell not yet built is read;
- the transition read is cut at 96 equipment buildings (`transition_targets_truncated`), and a building past the cut is
  unassessed;
- the `layout_edit` rows for the four transition reasons have no automated reader;
- the native reinstall, the uninstall designation, the storage read, `PenGrazing.Boundary` and `Animals`, and the per-worker
  transition legs have not been run in a game: no live-game verification of milestone 8 exists.
