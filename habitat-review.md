# Review: redefine-habitat-requirements (landed 88488f15e..a8d4ba50a)

Scope reviewed: the nine squashed milestone commits on main, 480 files, about 31k lines added and 7.5k deleted. Three independent readers covered Policy/Runtime/Store, the native bridge and acceptance cases, and the evidence page against the tests. I verified every finding marked **verified** by reading the code path myself. Findings marked *reviewer-confirmed* were traced end to end by a reviewer but not by me. *Plausible* means the mechanism is real but the failing scenario was not traced to the end.

## Verdict

The change does what the spec asks in structure, and I would not revert any of it. Every area now has one shared comparator with a real baseline (Retain or Defer) that keeps ties, unread facts are named rather than collapsed to zero, bindings are intent on the layout plan and are dropped from fresh facts rather than receipts, and there is no central planner, no second ledger and no omission-driven removal left. Strict OpenSpec validation and the full gate pass.

It should not be archived yet. Four defects would misbehave in a real colony, two of them in the native reads that feed everything else, and the evidence page's headline tally is generous. None of this is large. The fixes below are each a focused commit.

## Fix before archiving

1. **Fixed 2026-11-01: bridging had no ledger, so one replacement was counted for several removals.** `ColonyReading.Bridging` now carries one `TransitionBridgingLedger` per review; `TransitionFacts.Admit` takes each admitted removal's bed slots and net generation on it, and every later check reads against what is left. Within one bed, owners take slots in id order, so a double bed's two owners need two. Regressions: `TransitionBridgingTests.AFreeSlotServesOneSleeperAcrossABedsOwnersAndAcrossTheReviewsRemovals` and `.TwoProducersOnOneNetCannotBothBeRemovedAgainstTheSameRemainingGeneration`. Limit: the two Policy callers that build their own facts (`HostSharing.HostRetireOwed`, `SleepingPlanningRules`) get a fresh ledger each; the three Runtime paths share the reading's.

2. **Fixed 2026-11-01: the servicing read cut its targets at 32.** Any not-full refuelable ranked above a damaged building, so a tribal colony with 33 torches never got a damaged wall into the read. Neither the proposal, the spec nor the tasks asked for a bound; the implementing agent authored it to cap game-thread path searches the path layer already avoids. The bound, the ranking and the `servicing_targets_truncated` finding are removed; the read lists every target and runs inside `NativePathLegs.Batched`, so its legs are searched on the job workers and kept across reads.

3. **Fixed 2026-11-01: the transition read cut its buildings at 96, and fence segments filled it.** Same origin as item 2. The bound and the `transition_targets_truncated` finding are removed, and a fence segment (`BuildingProperties.isFence`) fails the equipment filter, since it is the pen's boundary. Sandbags and barricades still list as equipment; they are structure in the defense family's sense and could be filtered the same way, a small follow-up.

4. **Fixed 2026-11-01: relocating a broken-down building was priced as if it restored service.** Relocate now loses the service over the horizon when `BrokenDown`, as Retain and Store do, so the fix wins. Regression: `TransitionChoiceTests.RelocatingABrokenDownBuildingMovesItBrokenSoTheFixWinsAndTheMoveLosesTheHorizon`.

## Fix soon, lower impact

5. **Fixed 2026-11-01: the defense price read coverage only.** A staffing shortfall is now `Assessed(able)` with its state named (`staffing:<needed>/<able>`), and a mortar with no shell is `Assessed(0)` (`shells:<turret>`), so both enter the price through the seats and turrets that do reach instead of leaving the row unassessed; equal prices order by more shots, then more cover (`DefenseChoiceRules.Orders`). Firepower and cover are still not converted to loss stopped: the model has no fight, and the design's `Covered` is the stated stand-in. Regressions: `DefenseChoiceTests.AnUnderstaffedLineIsPricedOnItsStaffedSeatsCoverage`, `.EqualPricesOrderByMoreShotsThenMoreCover`, `DefenseProtectionTests.StaffingExcludesTheViolenceIncapableAndTheDowned`, `.RiflesNeedNoAmmunitionAndAMortarCountsItsShells`.

6. **Fixed 2026-11-01: the bound defense baseline skipped the route cost.** `Retained` prices an unbuilt bound choice with `Routes` as `Priced` does (`C + R + ...`); an unprobed route leaves it retained naming the fact. Regression: `DefenseChoiceTests.ABoundUnbuiltBaselinePaysItsRoutesLikeAnAlternative`.

7. **Fixed 2026-11-01: care loss ignored the facility's current power state.** `PowerValuationRules.CareLoss` takes `poweredNow` like `WorkshopLoss`: unpowered now, the read factor already lacks the offset, so the powered factor is the read one plus the offset and the unpowered one the read factor. Regression in `PowerUtilityTests.AShortWinterNetworkOfABenchAPatientsMonitorAndABedroomHeaterBuilds` (the dark monitor reads the same loss from a 0.95 factor as the lit one from 1).

8. **Fixed 2026-11-01: the relocation haul was a straight line.** New request-driven haul probe (`observations_read_haul_probe`, `HaulProbeRequest`/`HaulProbeSnapshot`, `IGameApi.ReadHaulProbeAsync`, `IHaulProbeSource`, `NativeHaulProbe`): the reconcile names the legs it would price (`ReconcileRules.UnprobedHauls`, each building, its read worker and the two cells), the room reconcile asks the native for them in one batched read on the path job workers (`NativePathLegs.Batched`, legs kept across reads) and runs again with `TransitionFacts.Hauls` answering. An unprobed haul is unread (`haul_unprobed`), so Relocate is unassessed rather than guessed; the octile distance now only orders the open slots. Regression: `TransitionServiceTests.AMinifiableBedRelocatesIntoAnOpenBedSlotRatherThanTheSlotBeingBuiltNew` (extended). Limit: the probe walks the map as it stands; the slot's cell is unbuilt ground, which is what the haul crosses.

9. **Fixed 2026-11-01: a running repair blocked the breakdown fix.** Underway work is one alternative compared with the rest for all of the target's deficits and continued only when it wins; `ServicingDecision.Underway` still records it and holds its pawn (`ServicingChoiceRules.Continues`). Regression: `HomeUpkeepFamilyTests.ARunningRepairDoesNotHideTheBreakdownFixWhenTheFixWinsTheComparison`. Side effect, intended: an unneeded building with a running repair can lose to Retire.

10. **Fixed 2026-11-01: component and fuel stock were counted per family.** `ServicingLedger` (`ColonyReading.Servicing`, one per reading like the bridging ledger) holds the underway rows and each target's chosen hold; home upkeep, power restoration and defense rearm take the component, the fuel and the pawns from it, and a repeat `Decide` on the same ledger changes nothing. Regression: `PowerRestorationTests.TwoRequestsOfOneReviewSharingALedgerSpendTheSameStockOnce`. Limit (documented in upkeep-contracts): the ledger is the reading's, so families that observe their own `ColonyReading` in one review share stock only where they share the reading.

11. **Fixed 2026-11-01: the tend read scanned the medicines per bed.** `NativeSupportActivities` scans the medicine stacks once per doctor × patient and reuses them across that patient's candidate beds.

12. **Fixed 2026-11-01: saved raid trails grew without bound.** A trail holds each cell once (`Track.Walk`), so a track is bounded by the map however long the lord lives; nothing is cut.

13. **Smaller items, each decided 2026-11-01.** A slot a bound Relocate from another room moves its piece into is now held by that room's reconcile (`slot_bound_to_relocation:<target>`, neither built nor installed from stock; `TransitionHandsTests`), closing the race the review suspected; in the review that first decides the move, before the plan binds it, the other room may still owe the slot's build, and the native refuses the second placement. Spare-bed removal's `UnneededSpare` exclusion on `service ≤ 0` is kept as written: the spare is compared, and a known zero service is a fact, not an omission. A turret with no planned tier stays a named hold (`defense` unserved): removing a standing defense with no planned replacement is exactly what RG-HABITAT-011 forbids. An issued refuel now carries its fuel def and items on its `RecoveryService` payload and holds them from the ledger (`PowerRestorationTests.AnIssuedRefuelStillHoldsItsFuelWhenItsCensusOptionIsGone`). The job def names and `ConnectMaxDist` have kept-constants rows; the raw 60000 and 3600000 are `GenDate.TicksPerDay` and `TicksPerYear`; the bed cap and `FieldSpotRadius` are gone (below). Power network ids are the topology's `NetId` (the net's hash), so a planned connector's network matches the power topology's.

## Bounds nobody asked for

Neither the proposal, the design nor the tasks asked for a size bound on any read or choice; the implementing agents authored them, and each cut facts or capped capacity. Every one was removed on 2026-11-01, and the design's only sanctioned bounds (the layout search's trial counts, which report what they skip) now have kept-constants rows.

| Removed | Was | Now |
| --- | --- | --- |
| Servicing read targets | 32, refuelables ranked first | every target (item 2) |
| Transition read buildings | 96, fences counted | every equipment building (item 3) |
| Defense probe request | 16 candidates, 8 firing, 24 approach, 4096 pieces | no bound; native refuses only an unreadable row |
| Probed raids, approach stride | newest 3 raids, every 3rd cell | every observed ground raid; approach cells defined by weapon range of the engaged cells, plus each walk's end |
| Kept ground raids | newest 8 finished | every finished ground raid; a trail holds each cell once |
| Care candidate beds | 4 medical and 2 other, `care_beds_truncated` | every fitting bed, nearest first |
| Field treatment spots | `FieldSpotRadius` 4.9 | `GenRadial.MaxRadialPatternRadius`, the game's own radial |
| Pen feed store | at most 16 cells | the gross need over the stack's nutrition per cell |

Kept, with rows: `ActivitySiteRules.MaxRoomTrials` (6), `LayoutHospitalBedRules.MaxDefTrials` (3), `LayoutHostRules.HostTrials` (`SiteSearchTop`, 8), the design's "explicit computation bounds" whose truncation is reported as skipped decisions. Pre-existing and outside this change: the maintenance Methods' "at most eight targets and eight workers" in upkeep-contracts predates the change and was not touched.

## What checked out

- Utility pricing (`UtilityChoiceRules.Priced`): Defer keeps ties; lead, runway and exposure handled consistently.
- `ProspectiveCapacityRules.Choose`, the field ledger (pending holds from both owners, deterministic tie-break), and MethodClaims pawn slots for repair, fix and refuel.
- Transition binding: `Holds`, `Settled`, `Stale`, `SettledOpen`; a bound read-back never re-runs selection; a gated decision drops its binding; the codec round-trips and schema 78 has no shim left.
- Native literals against a decompile of RimWorld 1.7.4995: repair warm-up 80, 20 work per HP, construction factor 1.7, fix 1000 ticks, refuel 240, deconstruct clamp, `Blueprint_Install.WorkTotal` 150, octile 1.41421, gestation days, cover block chance bands and factors, and the line-of-sight validator (bounded work, correct shooter/target).
- Pens: `ConnectedRegions` is read only when the marker is enclosed. Ops: power switch drives `CompFlickable`'s own toggle, Allow/Rescue/LayDown validate before acting and refuse rather than throw.
- `upkeep/restore-service` and `redevelopment/replace-before-remove` assert the brain's normal path and reject a premature close; neither can pass vacuously.

## Evidence page (docs/requirements/habitat.md H8 to H16)

- Tests cited 168, found 167 (the one miss is a game symbol, not a test). No shape-only assertions behind behaviour claims. The limits section is accurate: all seven native cases this change added are OnDemand, run by neither the gate, `test -full` nor the Nightly; the rig does not drive `ColonyReview.StepAsync`; nothing ran in a live game.
- Corrected 2026-11-01: H16 now defines Complete as every clause asserted and counts 21 Complete, 5 Partial (open), 1 Partial (b). The earlier headline "26 of 27 scenarios Complete" was generous by about six. These scenario clauses have no test asserting them: 002 connection and temperature clauses; 003 the no-user path (the cited test is a different site); 005 surroundings and competing use; 008 the shared-producer dependence (the cited test proves a stock is spent once, not the dependence, and H13's own gap list says it is unpriced); 011 that comfort credit does not substitute for function; H13's "filled from the thermal read". The barracks stand-in row rests on a rig test that hand-sets the sheltered flag, which is within the rig's documented limit but is policy-level, not a native read. Read "Complete" as "policy-level covered in the rig", not "behaviour verified".
- The carry-forward gap list is reliable: every named reason string is still emitted, nothing is listed closed that is open or open that is closed. One admitted hole: H16 says it did not check the heater H9 lists as deferred, and that is not in the gap list.
- Acceptance cases weaker than their headers: `medical/field-treatment` passes on vanilla doctoring alone (it only checks the outcome, which a vanilla doctor produces); `defense/temporary-cover` closes on one standing sandbag of five and never reads the raid track its header cites; `power/configure-and-route` never checks that a conduit was built; `custody/population` accepts any refusal reason for `laydown-guest`.

## Suggested order

Items 1 to 13 and the H16 tally are done, each with a policy-level regression where one applies (the native changes, items 8, 11 and 12, have no gate-run test: the probe, the scan and the trail are exercised only by the OnDemand native cases). The authored bounds are removed. Archived after the gate.
