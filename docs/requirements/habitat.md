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
Evidence H1–H2, H6.

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
maintenance are supported. Ordinary repairs and door-to-wall replacement are
explicitly held (H5). Resilience across repeated losses needs further evidence.
Evidence H3–H6.

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
Evidence H7 and H8 prove the policy path; they do not establish sustained native colony performance.

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
  asserts `WaitFuel` for an empty producer with adequate capacity. Registered
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

HomeUpkeepFamily.Propose deliberately holds ordinary repairs and door-to-wall
replacement. Its tests assert those holds. These are implementation gaps tracked
in [#4006](https://github.com/davidarcher/rimgovernor/issues/4006) and
[#4007](https://github.com/davidarcher/rimgovernor/issues/4007); the working Home-area
restoration case does not prove either action.

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
choice. The legacy workshop ladder remains for a Need with no layout plan or no
owned unhosted load. The [spatial](../developers/contracts/spatial-contracts.md#layout-planner),
[forecast](../developers/contracts/forecast-contracts.md#workshop-and-laboratory-bench-credits)
and [essential-service](../developers/contracts/essential-services.md#hosted-workshop-loads)
contracts own the behavior.
