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
**Implementation:** Supported for the sampled first-room path. Growth and loss
recovery remain unassessed. Evidence H1–H2.

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
**Implementation:** Unassessed across the full outcome: power choice and native refuelling sampled;
temperature, maintenance and autonomous coordination need further tracing. Evidence H3.

## Evidence and inspection coverage

- **H1 — Housing work.** [ShelterRounds](../../brain/src/RimGovernor.Brain.BuildingRuntime/Rounds/RoundsShelterFamily.cs)
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

Food preservation connects to [food](food-security.md); service recovery connects
to [protection](protection-and-recovery.md). Room upgrades, couples, extreme
climates and large-colony layout remain unassessed. Prioritize the existing
first-room evidence before adding broad housing tests; see
[T7](review-queue.md#t7--finish-shelter-and-essential-care).
