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
a slave bed); accepted hosting quests' lodgers count before they arrive; shared,
private and held barracks are compared as layout alternatives; furnishing is built
only when its owners' gain beats its cost; and each herd's open pen, barn and
animal sleeping spot are compared by exposure deaths avoided
([H9](#h9--hosted-occupants-bed-sharing-and-herd-shelter)). Barn execution (task 6.2),
a human pregnancy fact, hosted babies in the nursery's sizing and the room-quality
minimums remain gaps. Milestone 4 adds that a medical bed is no free sleeping slot, a
patient held in one counts once, and a bed a care transport holds is its patient's
([H11](#h11--care-locations-compared-by-readiness-movement-delay-and-outcome)).
Evidence H1–H2, H6, H9, H11.

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
component source are not modelled. Resilience across repeated losses needs further
evidence. A hosted guest's or slave's cold or hot sleeping room is temperature work like a
colonist's (H9). Evidence H3–H6, H12.

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
Evidence H7, H8 and H10 prove the policy path; they do not establish sustained native colony performance.

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
matching, reach), babies as one crib each, accepted hosting quests' lodgers as known
arrivals, the hospitality gate counting vacant compatible slots, and the jail never
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
Known gaps, stated rather than priced: no human pregnancy fact is read, so an expected
birth is not crib demand; a prisoner outside a cell is not fed by wardens and its hunger
is not priced (`prisoner_unfed_outside_cell_not_modelled`); the nursery's sizing
(`ChildRoomNeeds`) counts colonist babies only, hosted babies reaching crib demand only
through the station graph; the room-quality minimums for bed replacement, beauty floors,
sculptures and common rooms stay fixed and dining and recreation mood is unpriced; and
the barn is planned and credited but not executed (owner step, animal flap at the pen
link, spot assignment and heater are task 6.2 of `redefine-habitat-requirements`).
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
  but was not executed; the nightly `-full` run or task 9.4 executes it. No run has read the native servicing census, so
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
  rate of a per-use comp is named (`fuel use rate:<def>`), not priced, so a turret's refuel is still held
  (`unsupported_forecast_turret_refuel`);
- the supply legs assume the nearest reservable stack serves every load (`servicing_supply_nearest_stack_legs`);
- deconstruction needs a designation, worked by native labor, so a replacement depends on both giver work types
  being active;
- the servicing read lists at most 32 targets; a larger set is cut with `servicing_targets_truncated` and the absent
  targets are unassessed, not unread;
- the private driver constants (1.7 work per speed, 80 and 20 repair work, 1000 fix ticks, 240 refuel ticks, the
  industrial component) are copied from the decompiled drivers.

Provision stays separate from outcome as RG-HABITAT-011 requires: an accepted order, a chosen option or a staged
acquisition establishes nothing, and an unread fact is named rather than defaulted.
