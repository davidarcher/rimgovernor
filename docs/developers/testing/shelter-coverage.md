# Shelter coverage map

Which check owns which shelter claim, so a planner change is proven by the
cheapest check that can prove it and the native cases keep only what needs a
real game. The same split is the pattern for other building routines:
shape-blind properties and every input combination below the game boundary,
one honest end-to-end path, and staged fixtures for recovery.

## Below the game boundary

The shelter planners are pure policy functions over recorded or built facts
(`brain/src/RimGovernor.Brain.Policy`, `brain/src/RimGovernor.Brain.BuildingRuntime`). xUnit in
`brain/tests/RimGovernor.Brain.Domain.Tests` holds the plan-level claims: `FirstLayoutPlanTests` (the first plan
latches Cold or Hot from the outdoor curve and hosts research at the catalog's research bench), `FirstRoomPlanTests`
(a first plan's rooms are the station graph's: bedrooms on a temperate map, one barracks on an ice sheet),
`SleepingStandInTests` (the day-one stand-in spots and their clearing) and `SleepingPlannerTests` (bedroom-wing moves
once everyone owns a bed outside a bedroom; a Greedy or titled colonist's suite and its larger successor). A
one-colonist cold start's later replans and the sixty-day solo year have no native case: a year cannot be staged
inside the acceptance tick cap.

## Native acceptance (`acceptance run shelter/<case>`)

| Case | What only a real game proves | Staged |
| --- | --- | --- |
| `layout/first-room-temperate` | A new temperate colony records its first plan and a built sleeping spot stands for every colonist before any roofed proper room. | wood |
| `shelter/manhunter_waitout`, `shelter/toxic_fallout` | A real threat beside a roofed hut: the served brain restricts every colonist (and, under fallout, a tame dog) to the Safe area, where they stand unhurt in the hut; once the threat ends the restrictions are lifted. | the hut, the pawns and the threat |
| `shelter/service-investment` | A cold occupied room's partly funded heater frame is finished, powered and warms the room while a competing lower-tier wall frame stays unbuilt. | the room, frames and plan |

A built room a replan drops leaves its ground to clearance; `clearance/retired-ground` owns that native clearance
(its footprint the injected plan's `RetiredGround`, walls, door and spots deconstructed until nothing of the colony's
stands there).

## Composition

The shelter cases run the `shelter,sleeping` families alone, which keeps
them minutes long, so they cannot catch a competing-concern or executor-wiring
defect. Multi-day composition evidence (every routine family on the player
control path, asserting indoor sleeping capacity for every colonist) is the
long-horizon run tracked in #2351.

Retiring a native check needs the replacement assertion landed first, a note
of where each assertion went, and a negative control showing the replacement
fails for the behavior the old case caught.
