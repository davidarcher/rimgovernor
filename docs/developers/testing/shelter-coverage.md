# Shelter coverage map

Which check owns which shelter claim, so a planner change is proven by the
cheapest check that can prove it and the native cases keep only what needs a
real game. The same split is the pattern for other building routines:
shape-blind properties and every input combination below the game boundary,
one honest end-to-end path, and staged fixtures for recovery.

## Below the game boundary

The shelter planners are pure policy functions over recorded or built facts
(`brain/src/RimGovernor.Brain.Policy`, `brain/src/RimGovernor.Brain.BuildingRuntime`). xUnit in
`brain/tests/RimGovernor.Brain.Domain.Tests` holds the plan-level claims: `ShelterLayoutPlanTests` (the first plan
latches Cold or Hot from the outdoor curve, holds one shelter sized for the colony and clear of every other room and
no laboratory, and the shelter template holds the climate's campfires or cooler on its interior, the building going
to the room that owns it) and `SleepingPlannerTests` (bedroom-wing moves once everyone owns a shelter bed; a Greedy
or titled colonist's suite and its larger successor). A one-colonist cold start's later replans and the sixty-day
solo year have no native case: a year cannot be staged inside the acceptance tick cap.

## Native acceptance (`acceptance run shelter/<case>`)

| Case | What only a real game proves | Staged |
| --- | --- | --- |
| `shelter/bunks-first` | The complete path: on a lab that houses nobody (asserted), with ComplexFurniture finished and wood beside the colonists, the served brain raises the shelter by ordinary pawn work, the game roofs it, and the native room census then holds a built Bed per colonist inside a roofed proper room. | research, wood |
| `shelter/retirement` | A retired shelter hut (its footprint the injected plan's `RetiredGround`) with its spots is cleared by pawns: walls, door and spots deconstructed until nothing of the colony's stands there. | the hut, its spots and the plan |
| `shelter/manhunter_waitout`, `shelter/toxic_fallout` | A real threat beside a roofed hut: the served brain restricts every colonist (and, under fallout, a tame dog) to the Safe area, where they stand unhurt in the hut; once the threat ends the restrictions are lifted. | the hut, the pawns and the threat |
| `shelter/service-investment` | A cold occupied room's partly funded heater frame is finished, powered and warms the room while a competing lower-tier wall frame stays unbuilt. | the room, frames and plan |

## Composition

The shelter cases run the `shelter,sleeping` families alone, which keeps
them minutes long, so they cannot catch a competing-concern or executor-wiring
defect. Multi-day composition evidence (every routine family on the player
control path, asserting indoor sleeping capacity for every colonist) is the
long-horizon run tracked in #2351.

Retiring a native check needs the replacement assertion landed first, a note
of where each assertion went, and a negative control showing the replacement
fails for the behavior the old case caught.
