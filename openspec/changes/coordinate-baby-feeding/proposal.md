# Proposal

## Superseded execution design

The maintainer's 2026-10-26 direction and [native-led labor change](../native-led-labor-execution/proposal.md) supersede advance caregiver selection/release in this proposal. Its implementation checkpoint `ff5cfc77b` is local and unlanded; native feeding acceptance remains unproved and #4040 remains open. Preserve the confirmed food requirements and useful observations. Continue implementation through the labor change, not this obsolete execution plan.

## Why

[Issue #4040](https://github.com/davidarcher/rimgovernor/issues/4040) identifies a missing path between baby-food provisioning and actual assisted feeding when caregivers hold governor work. The confirmed RG-FOOD-008 requires suitable food and available help; a capable breastfeeding pawn or stocked ingredients cannot establish that a baby will receive nutrition.

## What Changes

- Observe each baby's hunger, feeding progress, available care and feeding blockers through the existing native facts. Let vanilla choose and execute its legal feeding, movement and safe return.
- Make caregiver capacity available when feeding needs justify changing governor work. Coordinate that response through the existing shared Need/Method lifecycle, protect active care through safe return, and reassess failures or lack of progress.
- Remove the blanket assumption that any breastfeeding-capable pawn establishes adequate feeding. Assess usable nursing and suitable alternative food from current conditions and actual outcomes; retain unmet demand and explain blockers.
- Add illustrative RG-FOOD-008 scenarios covering contested caregivers, unavailable nursing, shared resources and interrupted feeding. Preserve its confirmed requirement text and existing scenarios.
- Prove the blocked-caregiver response and both native feeding modes with focused tests and short native cases under normal work selection. Add further prediction or execution constraints only when a concrete failure demonstrates the need.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `food-security`: elaborate the existing RG-FOOD-008 assisted-feeding obligation with baby-specific scenarios. This implements confirmed intent; it does not change the requirement's policy or claim full support for all dependents.

## Impact

Reuse the native baby-care read for feeding observations and extend it only where an actual decision or outcome check needs a missing fact. Pure Policy assesses unmet feeding and the need to make room for care; Runtime coordinates the existing Method lifecycle, Hands carries any required changes to governor orders, and vanilla owns childcare execution. Shared resource reads and claims must reflect current native care rather than continue promising the same caregiver to competing work.

Likely affected areas include `BabyFeeding`, `NativeBabyCare`, shared Method admission/continuation, incumbent release and `NativePawnActivities`. No new scheduler, persistence store, model call or paid service is required. Exact food-stack bindings, internal inventory-selection hooks, a milk-allocation solver and detailed seating/travel forecasts are not prerequisites. Update the owning contracts and food evidence alongside the behavior change.

Scope is the on-map baby-feeding path, including both native feeding modes, multiple babies competing for finite capacity, alternate provision, interruption and unavailable-care handling. Patient feeding, world travel and other unassessed food obligations remain separate work; this change must not report RG-FOOD-001 or RG-FOOD-008 wholly implemented.

## Authority and Evidence

The standing food spec and its 2026-10-25 maintainer decision record own intent. This proposal follows the user's explicit request to plan a fix for #4040. Its scenarios and technical design are proposed acceptance elaborations, not new approved feeding policy. The maintainer subsequently explicitly requested revising this change around vanilla-owned feeding, observation and reactive coordination, preserving the food requirements and requiring concrete failure evidence before adding prediction or execution constraints. That instruction supersedes the original design's demand for exact feeding inputs and detailed forecasts.

The audit at `e9d8d0770` identified the gap from code; planning was checked against `9a38be6f9`. No starvation reproduction or new passing gameplay run is established. The existing `child/first-child` case restricts families to bill, shelter and dialog; its checks cannot prove feeding amid normal competing work. Run 40193999645 reports that case failed without establishing this gap as the cause.
