# Real combat host

RimWorld's own combat code, run headless. The hand-ported combat sim (`go/internal/combatsim`,
`brain/src/RimGovernor.Brain.Simulation/Combat`) re-implements game logic that can drift from every game update;
this host runs the game's logic itself, in a bare `net10.0` process (Waylith has no Unity), so there is nothing to
port. Tracking issue: #3142. Nothing tunes policy at runtime, so the engine is offline only: one game per process,
no live-game integration.

| Project | What it is |
|---|---|
| `src/RealCombat` | Library. `GameHost.Boot` loads every def, the language and a seeded world; `Arena` builds a map, sweeps it and fights it once per seed. `Protocol.cs` is the request/response shape. `skips.txt` lists the UI, audio and non-combat tickers Harmony skips so the tick runs without a window. |
| `src/RealCombat.Host` | The process: `RimGovernor.RealCombat.Host --game <dir> --request <json> --out <json>`. |
| `src/RealCombat.Native` | Library. `DefenseStage` raises a ring-walled compound and the planner's killbox in it. `NativeSeat` hosts the native mod's own `CombatMirror` (observation) and `NativeCombatOrders` (orders) over an arena map, so a controller sees and commands a simulated fight through the code it uses against the live game. Bridge exposes its internals to it (`InternalsVisibleTo`). `Arena.Controller` is the hook that plays the colonists instead of the built-in retargeting (the hostiles then act under an assault lord, as a raid does). `PolicyController` is the controller that decides with the live policy. |
| `src/RealCombat.NativeSmoke` | Throwaway: a duel observed and commanded through the seat, or with `--policy` a 2v1 played by the live combat policy (`PolicyController`) inside a staged killbox compound (`DefenseStage`). |

## Build and run

The game copy must match the runtime's `Assembly-CSharp` (the private copy under `.rimgovernor/native-rimworld`, whose
`Data` is a junction to Steam). Where `RimWorldDir` has no `Assembly-CSharp.dll` the projects build as stubs.

```bash
dotnet build integrations/rimgovernor-realcombat/src/RealCombat.NativeSmoke -c Release -p:RimWorldDir=D:/rimgovernor/.rimgovernor/native-rimworld
dotnet integrations/rimgovernor-realcombat/src/RealCombat.NativeSmoke/bin/Release/RimGovernor.RealCombat.NativeSmoke.dll --game D:/rimgovernor/.rimgovernor/native-rimworld --policy
```

`--bare` plays both sides with the built-in retargeting, `--policy` plays the colonists with the live combat policy,
`--trace` prints each decision and its results, `--open` fights on open ground with no layout.

## The native seat

`NativeSeat.Open(map)` is everything the mod's combat code expects of a game and a headless arena lacks: a `ColonyIdentity`
component, `NativePawnControlState.Initialize` (the draft-ownership and order hooks its readiness audit checks), the
mirror's Harmony hooks, and control authority granted in `Auto` mode. `Find.CurrentMap` stays null: with a current map the
game turns on view-gated effects (motes, flecks, sounds) that need a camera, and shooting silently stops. The authority
reads its map through `NativeControlAuthority.Capture`, which the seat redirects to the arena map. Orders need the pawn
drafted by an order (`Draft`) first: the handler refuses `not_drafted` for a pawn the seat did not draft. Building this
project builds the Bridge project, which writes its gitignored `BridgeTools/RimGovernor` deploy folder.

## The live policy in the loop

`PolicyController` decides a fight with the code that decides one in the game. Every ten ticks it captures a snapshot
frame with the mod's own `SnapshotFrames.Capture`, publishes it to a facade (`PolicyFacade`, a `DispatchProxy` over
`INativeFacade` that serves only the catalog, the frame stream and the stat service; any other call throws by name) and
reads it with the brain's own `FacadeNativeClient.ReadCombatAsync`. `BuildingRuntimePackage.DecideCombatStep` then runs the
defense round's chain (frame inputs, `CombatView`, the newest stop, `DecideCombat`) and builds the wire batch, which
`NativeSeat.Apply` hands to the order handler. It decides at the first check, at each stop event and at a 300-tick backstop.

Geometry asks are answered from the arena map through the mod's own `NativeCombatGeometryTools`. The defense layout comes
from `DefenseStage`, which stages a compound as the colony's planner expects to find one: a ring wall across the map but
for the killbox's opening (`OpeningPlan`, `LayoutKillbox`), a census of every cell in the killbox region read off the map,
and `PolicyFunctions.DefenseLayouts` over it. Every building the planner proposes (funnel and back walls, the defenders'
door, spike traps, fences, sandbags) is placed at once, so a fight starts on the finished defense, and the firing line,
retreat cells, direction and choke the policy holds are read off the result the way the defense round reads them
(`PolicyController.Layout`). `--open` withholds all of that: open ground, no layout, the squad fallback.

What it does not do yet: feed burn, royalty and drug inputs. The policy fields two defenders per opponent, so a 1v1 duel is
never one of its fights. A compound fight is not yet reproducible across processes: the raider's first job against the ring
(ignite a wall, or attack it) differs from run to run, so the same seed gives 619 to 3000 ticks. Open-ground fights are.

## Determinism

A seed fully determines a fight, within and across processes. The game's `Rand` is counter based, so pushing a seed
fixes every draw, provided nothing else moves between fights: thing ids and the tick counter restart from fixed
values, the world and map are generated under fixed seeds, the map is swept (things, roofs) and settled before the
baseline, the fight's things are destroyed afterwards and the world pawns the generator parked are removed.
`"verify": true` in a request plays every seed twice and flags a mismatch. A 1000-seed run is byte-identical across two
processes. About 8–12 ms a fight after a ~7 s startup.

## Known harness facts

- Pawns are generated with `mustBeCapableOfViolence`: without it about 16% of generated colonists carry a backstory
  that forbids violence, never fire, and make the colonist side look weak.
- A skip target that no longer exists in the game build fails the boot with its name, so a game update that renames one
  cannot silently run unskipped.
- Save/load is not a fork primitive: a reloaded fight diverges by a draw or two because some runtime counters are not
  saved. Fork by replaying the seed and the order log (about 4 µs a tick).
