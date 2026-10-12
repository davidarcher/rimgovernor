# Shelter coverage map

Which check owns which shelter claim, so a planner change is proven by the
cheapest check that can prove it and the native cases keep only what needs a
real game. The same split is the pattern for other building routines:
shape-blind properties and every input combination below the game boundary,
one honest end-to-end path, and staged fixtures for recovery.

## Below the game boundary (`go run ./cmd/test`)

| Claim | Check |
| --- | --- |
| Style selection by tech tier, unknowns included | `buildingruntime.TestShelterStylePicksByTier` |
| Every shell stands on its planned room (the shelter on the planned storeroom at every tier), refuses a blocked slot or a missing plan, reuses rock and player walls, marks rock for plan dig and claims matching ruins | `policy` `layout_rooms_test.go` (`TestPlannedLayout…`, `TestShelterStandsOnThePlannedStoreroom`), `buildingruntime.TestRoundsShelterRefusesABlockedPlannedStoreroom`, `…TestExpansionClaimsAMatchingRuinOnItsPlannedRing` |
| Shape-blind properties of every admitted ring: census-offered ground only, previewed whole, encloses the staged beds with an aisle to spare, opens south off its own shell | `buildingruntime.TestRoundsShelterShellInvariantsAcrossSites` |
| Bunk rungs before the ring; refused beds fall through to the shell | `buildingruntime` `rounds_shelter_bunks_test.go` |
| Whole-ring admission in one wave, no stock gate, spending rules and reserves | `buildingruntime.TestRoundsShelterAdmitsWholeShellInOneWave`, `…AdmitsShellWithoutStockCheck` |
| Partial and unknown inputs: unknown room, unknown terrain, unknown material, mismatched footprint, stale or superseded preview, player zone, occupied cell, late refusal — nothing committed | `buildingruntime.TestRoundsShelterNeverCommitsPartialOrUnknownShell` |
| Already roofed ground is no site | `buildingruntime.TestRoundsShelterRefusesAlreadyRoofedGround` |
| Existing indoor space is furnished instead of walled | `buildingruntime.TestRoundsShelterPrefersExistingRoom` |
| Roofing budget: observed completion only, never renewed, survives furnishing and retirement | `buildingruntime.TestShelterRoofingBudget…`, `…RoofingContinuesAfterFurnishing…` |
| Adoption matrix: missing cells only, a lone door, best-matched shape or wait, an earlier grown ring from its plan, a cancelled door, no match, repair under one epoch, a whole ring passed by | `buildingruntime` `rounds_shelter_test.go` (`…Adopts…`, `…Reissues…`, `…Repairs…`) |
| Manual cancellation of a pending ring | `buildingruntime.TestRoundsShelterManualCancelsWholePendingShell` |
| Starter-search siting per terrain (recorded searches) | `buildingruntime.TestShelterSitingSnapshots` |
| Which concerns open beside the initial shelter | `snapshot.TestReplay*Shelter*` |
| Retirement per capability: sleep, craft, research (bench outside, or a standing lab), heat on a cold map, unknowns and an otherwise empty plan keep the shelter | `policy` `layout_shelter_retire_test.go` (`TestShelterRetirable…`, `TestShelterRetiresForSoloColonyWithBenchInBedroom`) |
| A solo colony: the shelter hosts sleep, craft, research and heat, and no laboratory is owed while it has capacity | `policy.TestSoloColonyOwesNoLaboratory` |

A planner test's modeled reachability is not native reachability, and the
fast checks derive nothing from the production geometry helpers they cover:
interiors are flood-filled and roof support measured against RimWorld's
documented radius in the test itself. Shell adoption through a wood shortage
is not replayed.

## Native acceptance (`acceptance run shelter/<case>`)

| Case | What only a real game proves | Staged |
| --- | --- | --- |
| `shelter/bunks-first` | The complete path: an unhoused colony, the controller discovers the deficit, places spots, builds beds, raises the whole ring by ordinary pawn work, the game roofs it, and the native census then holds one bed per colonist inside. The precondition is asserted not to satisfy the outcome. | nothing |
| `shelter/solo-cold` | One colonist on a cold ice-sheet start: the plan latches Cold, holds the shelter and plans no laboratory, and the shelter's campfire stands on its interior. A cold start needs a real world; the capability logic itself is the snapshot rows above. | nothing |
| `solo/sea-ice-stock`, `solo/sea-ice-rhadamant` | Epic #2745's year: one colonist on a very cold ice sheet under Randy at the top difficulty (the second with Rhadamant's parts, `httpapi.RhadamantParts`). Played in ten-day serve segments; each boundary probes the reattached game for the colonist alive, Hypothermia at most serious (0.35), Malnutrition at most hungry, sleeping temperature inside the policy band (after the first segment) and, at the end, power on; the census food runway must never reach zero. The target is the 60-day RimWorld year (`policy.YearDays`, 3.6M ticks), the nightly default: at the measured 899 ticks/s Ultrafast it is about 67 minutes of play, so each case budgets 92 minutes (six segments at 3 minutes plus 7 fixed), 184 with the retry, inside the 345-minute suite allowance. `RIMGOVERNOR_ACCEPT_SOLO_DAYS` sets another length and the budget scales with it; 365 days exceeds the allowance, so it is a by-hand run only. | a scenario start |

## Composition

The shelter cases serve the `shelter,sleeping` families alone, which keeps
them minutes long, so they cannot catch a competing-concern or executor-wiring
defect. `campaign/foothold` is the normal-composition evidence: every
routine family on the player control path over three game days, asserting
indoor sleeping capacity for every colonist. Run the pair together: a
shelter change that passes one and fails the other is a composition defect,
not a planner defect.

Retiring a native check needs the replacement assertion landed first, a note
of where each assertion went, and a negative control showing the replacement
fails for the behavior the old case caught.
