# Shelter coverage map

Which check owns which shelter claim, so a planner change is proven by the
cheapest check that can prove it and the native cases keep only what needs a
real game (#615). The same split is the pattern for other building
routines: shape-blind properties and every input combination below the game
boundary, one honest end-to-end path, and staged fixtures for recovery.

## Below the game boundary (`go run ./cmd/test`)

| Claim | Check |
| --- | --- |
| Style selection by build tier and faction tech level, unknowns included | `buildingruntime.TestShelterStylePicksByTierAndFaction` |
| Template shape per terrain (circle, ovals, low ovals, concave L, connector, grown footprint), deterministic and input-order independent | `policy` `starter_layout_test.go` |
| Shape-blind properties of every sited shell over a table of sites and both styles: distinct in-bounds cells on offered ground, one door, a connected enclosed interior, roof support, a threshold on free ground, protected cells preserved | `policy.TestStarterShellInvariantsAcrossSitesAndStyles` |
| Shape-blind properties of every admitted ring: census-offered ground only, previewed whole, encloses the staged beds with an aisle to spare, opens south off its own shell | `buildingruntime.TestRoutineShelterShellInvariantsAcrossSites` |
| Bunk rungs before the ring; refused beds fall through to the shell | `buildingruntime` `routine_shelter_bunks_test.go` |
| Whole-ring admission in one wave, no stock gate, spending rules and reserves | `buildingruntime.TestRoutineShelterAdmitsWholeShellInOneWave`, `…AdmitsShellWithoutStockCheck` |
| Partial and unknown inputs: unknown room, unknown terrain, unknown material, mismatched footprint, stale or superseded preview, player zone, occupied cell, late refusal — nothing committed | `buildingruntime.TestRoutineShelterNeverCommitsPartialOrUnknownShell` |
| Already roofed ground is no site | `buildingruntime.TestRoutineShelterRefusesAlreadyRoofedGround` |
| Existing indoor space is furnished instead of walled | `buildingruntime.TestRoutineShelterPrefersExistingRoom` |
| Roofing budget: observed completion only, never renewed, survives furnishing and retirement | `buildingruntime.TestShelterRoofingBudget…`, `…RoofingContinuesAfterFurnishing…` |
| Adoption matrix: missing cells only, a lone door, best-matched shape or wait, an earlier grown ring from its plan, a cancelled door, no match, repair under one epoch, a whole ring passed by | `buildingruntime` `routine_shelter_test.go` (`…Adopts…`, `…Reissues…`, `…Repairs…`) |
| Manual cancellation of a pending ring | `buildingruntime.TestRoutineShelterManualCancelsWholePendingShell` |

A planner test's modeled reachability is not native reachability, and the
fast checks derive nothing from the production geometry helpers they cover:
interiors are flood-filled and roof support measured against RimWorld's
documented radius in the test itself.

## Native acceptance (`acceptance run shelter/<case>`)

| Case | What only a real game proves | Staged |
| --- | --- | --- |
| `shelter/bunks-first` | The complete path: an unhoused colony, the controller discovers the deficit, places spots, builds beds, raises the whole ring by ordinary pawn work, the game roofs it, and the native census then holds one bed per colonist inside. The precondition is asserted not to satisfy the outcome. | nothing |
| `shelter/hut` | Oval or grown geometry observed as one enclosed roofed room; adoption across a controller restart with exactly one cancelled wall reissued | ring, but a few load-bearing cells |
| `shelter/hut-corridor` | The same for a grown irregular shell confined to a five-cell corridor | as above |
| `shelter/hut-shortage` | A shell plan held through a material shortage with no second shell or order, then completed | as above |
| `shelter/hut-oval`, `shelter/hut-low-oval` | Strip terrain selection rules: the medium and low east-west ovals, with their doors on ground the game agrees is open | as above |
| `shelter/hut-concave`, `shelter/hut-connector` | Room, roof and passage observation for the composite templates: an L wrapping an obstacle and two chambers joined by one cell | as above |
| `shelter/excavation-hazard`, `-reroute`, `-breach` | Digging the room into mountain rock and reacting to what the dig uncovers | see each case |

The footprint variants are kept because each one is a different failure
mechanism in the game's own room, roof, doorway and access handling, not a
different planner branch: the planner branches are the table rows above.
Policy selection, restart and shortage are exercised on one footprint each
rather than on every footprint.

## Composition and the shortage fixture

The shelter cases serve the `shelter,sleeping` families alone, which is what
keeps them minutes long, so they cannot catch a competing-goal or
executor-wiring defect. `campaign/foothold` is the normal-composition
evidence: every routine family on the player control path over three game
days, asserting indoor sleeping capacity for every colonist. Keep that pair
together -- a shelter change that passes here and fails there is a
composition defect, not a planner defect.

`shelter/hut-shortage` takes only WoodLog, which is a true shortage rather
than a preference: RimWorld's frames demand the stuff their blueprint was
placed with, so stone, steel or chunks elsewhere on the map cannot finish a
wooden wall and no alternative material shortens the hold.

Nothing has been retired. Retiring a native check needs the replacement
assertion landed first, a note of where each assertion went, and a negative
control showing the replacement fails for the behaviour the old case caught.
