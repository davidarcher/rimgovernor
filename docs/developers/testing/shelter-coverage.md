# Shelter coverage map

Which check owns which shelter claim, so a planner change is proven by the
cheapest check that can prove it and the native cases keep only what needs a
real game. The same split is the pattern for other building routines:
shape-blind properties and every input combination below the game boundary,
one honest end-to-end path, and staged fixtures for recovery.

## Below the game boundary

The shelter planners are pure policy functions over recorded or built facts
(`brain/src/RimGovernor.Brain.Policy`, `brain/src/RimGovernor.Brain.BuildingRuntime`). The ported brain has no
shelter unit tests yet (`brain/tests` holds only a few bug-driven xUnit tests), so the native cases below carry
these claims: style selection by tier, shells standing on the planned room, whole-ring admission in one wave,
refusal of partial or unknown inputs, the roofing budget, adoption and retirement per capability.

## Native acceptance (`acceptance run shelter/<case>`)

| Case | What only a real game proves | Staged |
| --- | --- | --- |
| `shelter/bunks-first` | The complete path: an unhoused colony, the brain discovers the deficit, places spots, builds beds, raises the whole ring by ordinary pawn work, the game roofs it, and the native census then holds one bed per colonist inside. The precondition is asserted not to satisfy the outcome. | nothing |
| `shelter/solo-cold` | One colonist on a cold ice-sheet start: the plan latches Cold, holds the shelter and plans no laboratory, and the shelter's campfire stands on its interior. A cold start needs a real world. | nothing |
| `solo/sea-ice-stock`, `solo/sea-ice-rhadamant` | Epic #2745's year: one colonist on a very cold ice sheet under Randy at the top difficulty (the second with Rhadamant's parts, `RhadamantParts`). Played in ten-day segments; each boundary probes the game for the colonist alive, Hypothermia at most serious (0.35), Malnutrition at most hungry, sleeping temperature inside the policy band (after the first segment) and, at the end, power on; the census food runway must never reach zero. The target is the 60-day RimWorld year (`PolicyFunctions.YearDays`, 3.6M ticks), the nightly default: at the measured 899 ticks/s Ultrafast it is about 67 minutes of play, so each case budgets 92 minutes (six segments at 3 minutes plus 7 fixed), 184 with the retry, inside the 345-minute suite allowance. `RIMGOVERNOR_ACCEPT_SOLO_DAYS` sets another length and the budget scales with it; 365 days exceeds the allowance, so it is a by-hand run only. | a scenario start |

## Composition

The shelter cases run the `shelter,sleeping` families alone, which keeps
them minutes long, so they cannot catch a competing-concern or executor-wiring
defect. `campaign/foothold` is the normal-composition evidence: every
routine family on the player control path over three game days, asserting
indoor sleeping capacity for every colonist. Run the pair together: a
shelter change that passes one and fails the other is a composition defect,
not a planner defect.

Retiring a native check needs the replacement assertion landed first, a note
of where each assertion went, and a negative control showing the replacement
fails for the behavior the old case caught.
