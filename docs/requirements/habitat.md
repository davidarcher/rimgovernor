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
