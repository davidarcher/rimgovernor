# Proposal

## Why

[Issue #4040](https://github.com/davidarcher/rimgovernor/issues/4040) identifies a missing path between baby-food provisioning and actual assisted feeding when caregivers hold governor work. The confirmed RG-FOOD-008 requires suitable food and available help; a capable breastfeeding pawn or stocked ingredients cannot establish that a baby will receive nutrition.

## What Changes

- Include each baby's feeding demand and feasible breastfeeding/bottle-feeding alternatives in shared planning, with caregiver time, access, milk or exact food inputs and actual intake accounted for.
- Compare feeding with competing colony work and extend the existing revisioned release path where necessary to make a selected caregiver available safely. Do not depend on an idle pawn or a native childcare exception.
- Replace the blanket breastfeeding-pawn-count exemption with feasible feeding capacity when assessing baby-food provision. Retain unmet demand and explain blockers when feeding cannot be arranged.
- Add illustrative RG-FOOD-008 scenarios covering contested caregivers, unavailable nursing, shared resources and interrupted feeding. Preserve its confirmed requirement text and existing scenarios.
- Add focused planner tests and short native feeding cases with normal work selection; update the owning contracts and food evidence with their actual proof limits.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `food-security`: elaborate the existing RG-FOOD-008 assisted-feeding obligation with baby-specific scenarios. This implements confirmed intent; it does not change the requirement's policy or claim full support for all dependents.

## Impact

The native baby-care read and typed GameFacts need exact execution facts beyond today's conditional nursery exposure bounds. Pure Policy must compose the feeding alternatives and their consequences with shared demand, claims and incumbent comparison; Runtime, the Method journal, Hands, typed GameOrders and native activity execution must carry the selected service through to observed intake.

Likely affected areas include `BabyFeeding`, `NativeBabyCare`, coordinated candidate generation/binding, shared forecast consequences, incumbent release/continuation and `NativePawnActivities`. No new scheduler, persistence store, model call or paid service is required. The implementation must update support-work, pawn-activities, voluntary-control and any affected boundary/forecast contracts, plus the food evidence page.

Scope is the on-map baby-feeding path, including both native feeding modes, multiple babies competing for finite capacity, alternate provision, interruption and unavailable-care handling. Patient feeding, world travel and other unassessed food obligations remain separate work; this change must not report RG-FOOD-001 or RG-FOOD-008 wholly implemented.

## Authority and Evidence

The standing food spec and its 2026-10-25 maintainer decision record own intent. This proposal follows the user's explicit request to plan a fix for #4040. Its scenarios and technical design are proposed acceptance elaborations, not new approved feeding policy.

The audit at `e9d8d0770` identified the gap from code; planning was checked against `9a38be6f9`. No starvation reproduction or new passing gameplay run is established. The existing `child/first-child` case restricts families to bill, shelter and dialog; its checks cannot prove feeding amid normal competing work. Run 40193999645 reports that case failed without establishing this gap as the cause.
