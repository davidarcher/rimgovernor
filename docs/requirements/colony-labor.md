# Colony labor: implementation and evidence

[Collection](README.md) · [Labor requirements](../../openspec/changes/native-led-labor-execution/specs/colony-labor/spec.md) · [Design](../../openspec/changes/native-led-labor-execution/design.md)

## Intent and evidence ownership

The maintainer confirmed native-led routine execution, full control of settings and meaningful direct interventions in the 2026-10-26 discussion recorded in the [proposal](../../openspec/changes/native-led-labor-execution/proposal.md#decision-provenance-and-review-boundary). RG-LABOR-001–008 live in that change's delta pending explicit synchronization. This page owns implementation inspection and proof limits, not new requirements. Other requirements retain their IDs and ownership.

## Current evidence

Inspection at local checkpoint `ff5cfc77b` plus the existing worktree edits established native settings and work-creation operations, strict coupled-job interception, passive activity observations and shared lost-owner continuation. The first milestone below replaces the strict lifecycle interception and proves scoped feeding outcomes. All general routine migration entries remain pending. [Food evidence](food-security.md) retains earlier fixture failures and the distinct executed bottle/nursing results. #4040 remains open until the verified implementation lands.

The existing `ActivityContinuationTests.LostParticipantDoesNotAuthorizeReleasingItsNewOwner` and `KnownOwnershipEndRetiresIntentWithoutTouchingAnotherAssignment` exercise pure ownership/journal semantics. They do not prove that the native hooks permit takeover or that worker invalidation reaches the live game. Existing numeric priority, bill and designation operations establish available mechanisms, not their successful composition under this new design.

The first implementation removes advance caregiver release and holds capacity only
for actual care. It removes the strict constant/override/opportunistic/finalizer
interception and indefinite dependency-wait driver. An actual replacement job
detaches the exact execution handle and sends the existing pawn invalidation.
`NativeTakeoverReconcilesDependentWorkThroughTheJournalWithoutReleasingTheNewOwner`
and `NativeTakeoverInvalidationWakesTheWorkerAndDispatchesOnlyExactRemainingRelease`
cover journal cleanup and synchronized worker wake, including remaining-owner
revisions. The required gate passed 4,064 domain tests and 30 workflow tests,
production/fixture compilation and contract probes. This is not a completed
routine migration. Native results follow; [food evidence](food-security.md) records
the first bottle attempt's partial outcome and the passing bottle/nursing runs.

All of these native cases executed on 2026-10-26 against the worktree's private
package at dirty `ff5cfc77b`, source tree
`f8b7bc3f1f433ae51a7c934204ff9350eb3268d579aa27363e0aedf2ff054b0d`:

| Scoped native claim | Executed result / local provenance |
| --- | --- |
| Considering an alternative retains the handle; actual takeover detaches it and ending the replacement does not restart the old attempt | `control/activity-native-takeover`: PASS, 17.54 s; `.rimgovernor/labor-native-takeover/control/activity-native-takeover/result.json` |
| Native involuntary sleep, vomiting, imposed sleep and subsequent ordinary replacement use actual takeover | `control/activity-native-overrides`: PASS, 2.033 s; `.rimgovernor/labor-native-overrides/control/activity-native-overrides/result.json` |
| A dependency wait offers no indefinite governor job and permits native takeover | `control/activity-dependency-yields-native`: PASS, 1.808 s; `.rimgovernor/labor-native-dependency/control/activity-dependency-yields-native/result.json` |
| Initial harvest yields to native ingestion and sleep | `control/pawn-activities`: PASS, 2.724 s; `.rimgovernor/labor-native-sequence/control/pawn-activities/result.json` |
| Bottle feeding and nursing after real journal/Hands-owned competing harvest; actual intake without malnutrition and protection through post-feeding return | Separate PASS results, 24.94 s and 23.751 s, with complete provenance in [food evidence](food-security.md) |

These cases establish lifecycle mechanics and the staged feeding response. They
do not establish a native throughput baseline, justify direct interventions, prove
ordered native queues or close any routine migration entry. Existing exact
generators, preemptible-as-free accounting and target-holder preemption remain
identified work for the later groups. The unrelated observed layout logging
exception is tracked as [#4041](https://github.com/davidarcher/rimgovernor/issues/4041).

## Routine execution migration inventory

The inventory follows `ForecastCoupledCandidates.GenerateCoupledActivities`, `ForecastCoupledSelection.StartCoupledActivity` and `ColonyReview.AdmitCoupledSelection`. Every activity family below currently feeds the shared exact binding/admission path. A forecast can remain useful after its routine exact dispatch is removed. Each row must be closed with source and verification evidence; a planned target is not an implemented path.

| Current producer / dispatch responsibility | Target execution | Owning contract / verification required | Status |
| --- | --- | --- | --- |
| `FoodMethodCandidates.GenerateCoupled`: harvest/hunt, preparation and food transport | Native designations, bills, storage and priorities; direct work only for justified meaningful intervention | [Support work](../developers/contracts/support-work.md), [forecast](../developers/contracts/forecast-contracts.md), food evidence; provider/admission tests and short native usable-output claim | Pending |
| `ForecastNeeds.GenerateNeedsAlternatives`: eating, rest and recreation | Native needs, timetable and policies; preserve comparison of adequacy/cost and bounded justified exceptions | [Pawn activities](../developers/contracts/pawn-activities.md), forecast; needs/continuation tests without future exclusive holding | Pending |
| `ClothingMethodCandidates.Generate`, effect-source composition | Native production bills and work settings; explicit inputs and conditional yields | [Equipment](../developers/contracts/equipment-upkeep.md), forecast; stock/ingredient/partial-output regressions | Pending |
| `ConstructionMethodCandidates.Generate`: supply and build | Native blueprints, material availability and construction/hauling priorities; justified direct exceptions | [Facilities](../developers/architecture/facilities.md), [continuation](../developers/contracts/method-continuation.md); material, quality, skill and dependency tests | Pending |
| `CoupledShelterCandidates.Generate`, `PlannedRoomCandidates.Generate` | Retain room/investment selection; realize selected work through native sites and ordinary work settings | Facilities, forecast; room prerequisites and actual build/output evidence | Pending |
| `SupportMethodCandidates.Generate`: care, delivery, hauling and refill | Native work/settings and observed current occupation; justified direct exceptions | Support work, [medical care](../developers/contracts/medical-care.md); reachable native support and stale-care dispatch tests | Pending |
| `ResearchMethodCandidates.Generate`: exact research blocks | Existing research goal/current project plus native priorities; direct block only with meaningful justification | [Research](../developers/contracts/research.md), [work assignment](../developers/contracts/work-assignment.md); no forced routine block or research-only fixture dependency | Pending |
| `SowingMethodCandidates.Generate`: exact sowing work | Crop/zone choices and native growing priorities, with justified deadline intervention | Work assignment, forecast; native-ready sowing and deadline comparison tests | Pending |
| `PracticeMethodCandidates.Generate`: range and sparring | Native practice affordances/settings where available; bounded direct activity only for justified capability need | [Capability maintenance](../developers/contracts/capability-maintenance.md), [facilities](../developers/architecture/facilities.md); capability demand, actual native completion and interruption tests | Pending |
| Existing observed incumbent retention / `ForecastCoupledExecution` | Account for useful native work and actual intervention ownership; remove routine exclusive waiting/restarts | Pawn activities, forecast, continuation; native opportunity cost and lost-owner/dependency regressions | Pending |
| `AdmitSelectedTrade` and world/mission alternatives | Keep existing trade/journey decisions; account for unavailable home labor without redesigning travel execution | [Trade](../developers/contracts/controllable-trade.md), forecast; no fabricated home capacity or duplicate inventory promises | Boundary audit pending |

All routine activity types above share `StartCoupledActivity` / `CommitClaimedMethod` / Hands dispatch. Removing one family's exact binding does not remove another's. Trace final readers before deleting shared task types, native observations, codecs or helpers. Additional readers found while migrating belong in this inventory, not an undocumented exemption.

## First milestone and remaining proof

The first milestone must permit native takeover across selection/start/end/finalizer boundaries, reconcile actual ownership loss and let nursing/bottle feeding finish through safe return. It removes baby-specific advance pairing/release. Cases must stage deterministic ordinary owned work through the real journal/Hands path; they must not depend on the portfolio choosing an unrelated research goal. The harness must not release the caregiver or assign feeding after setup.

Later milestones complete capacity/native-baseline comparisons, meaningful interventions, finite native queue identity/recovery and every ordinary execution migration above. A passing feeding case alone does not implement the general labor requirements. The first milestone's executed claims are recorded above; no later-group behavior is certified by them.

Rules 7–9 review for the first milestone: policy owns finite provisioning and
lost-owner decisions; Runtime sequences the existing journal/Hands path; native
code observes actual jobs and emits the existing invalidation. Only actual care
withholds worker capacity, consumed inputs are not refunded, and missing participant
reads defer cleanup. No new scheduler, durable queue or care ledger was added.
The orphan sweep found no source readers of the removed advance-release helpers,
custom wait driver or source-scoped job permissions. Complexity: added one internal
live-journal fixture seam and one takeover invalidation producer; removed advance
caregiver release, the indefinite wait driver and five lifecycle interception paths;
orphans left: none. Existing routine-exclusive readers remain in the inventory,
not as orphan exemptions or claims of completed migration.
