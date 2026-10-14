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
`--drugs` gives each colonist go-juice (`--high` makes the raiders high on it, `--raiders N` adds raiders),
`--trace` prints each decision and its results, `--open` fights on open ground with no layout, `--no-traps` leaves the
planner's spike traps out of the compound so the raider reaches the firing line (otherwise it usually dies to the traps
first; with them out the colonists fire and win in 1644 ticks). `--armor` puts both sides in a flak vest and
helmet, `--melee` gives the raiders longswords, `--cover` lays sandbags in front of the colonists on open ground, `--psycast`
gives the first colonist the Stun psycast and moves the raider within the policy's 20 cell casting reach (`--policy --open --psycast`: colonist win, 734 ticks); `--permit` is described under Reads.

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

What it does not do yet: start a burn-out, or feed permits. The policy fields two defenders per opponent, so a 1v1 duel is
never one of its fights.

## Determinism

A seed fully determines a fight, within and across processes. The game's `Rand` is counter based, so pushing a seed
fixes every draw, provided nothing else moves between fights: thing ids and the tick counter restart from fixed
values, the world and map are generated under fixed seeds, the map is swept (things, roofs) and settled before the
baseline, the fight's things are destroyed afterwards and the world pawns the generator parked are removed.
`"verify": true` in a request plays every seed twice and flags a mismatch. A 1000-seed run is byte-identical across two
processes. About 8–12 ms a fight after a ~7 s startup.

## Known harness facts

- Filth is skipped (`skips.txt`): which neighbouring cell a drop of blood lands on, and whether a new filth spawns, varied from process to process and moved the draw count by one, which turned a long armored fight into 2513, 2517, 2899 or 3000 ticks.
- Standing things (a staged compound) take fixed thing ids: the game seeds some choices from a thing's id (a raider choosing to ignite a wall or attack it), and ids left to the map generator differ from process to process.
- Pawns are generated with `mustBeCapableOfViolence`: without it about 16% of generated colonists carry a backstory
  that forbids violence, never fire, and make the colonist side look weak.
- A skip target that no longer exists in the game build fails the boot with its name, so a game update that renames one
  cannot silently run unskipped.
- Save/load is not a fork primitive: a reloaded fight diverges by a draw or two because some runtime counters are not
  saved. Fork by replaying the seed and the order log (about 4 µs a tick).

## Reads beyond the frame

The defense round adds three reads to the combat view after the frame; `PolicyController` supplies them through
`CombatReads`. **Drugs**: the catalog's combat drugs in preference order, from the facade's definition catalog (a pawn's
carried drugs come from the mirror; `RealPawn.Carried` puts a drug in an inventory and `Hediffs` gives a starting hediff).
In the compound with `--drugs --high`, both colonists are ordered go-juice at the first stop with the raider in range and
no other order, and the handler applies it (one colonist is then overdosed and downed, the other rescues them to the bed and the fight stands off without a winner at 3000 ticks). **Burn**: while a burn-out is active the step surveys its site with the
native defense-site read, served from the arena map; nothing in the harness starts a burn-out yet (it needs an
infestation and a staged seal). **Royalty**: built as the rounds build it, from the frame's colony royalty section and pawn rows against the catalog
(`ColonyProjection.RoyaltyOf`), so it is known where Royalty is loaded. `RealPawn.Psycasts` gives a colonist a psylink, the
named psycasts and full psyfocus. The step's cast and permit orders leave the batch as Ability actions (`CombatStep.Calls`), and
`NativeSeat.ApplyAbility` runs them through the mod's `NativeAbilityOperations`. They go after the batch: a combat order issued after a cast replaces the cast job. The caster is not left idle: it stands in `Wait_Combat` and fires at will (same shot count as its partner). `RealPawn.Title`, `Favor` and `Permits` make an Empire title holder; with `--permit` the first colonist is an acolyte with the trooper squad permit against four raiders, the policy calls the aid at the first stop (outmatched) and the colonists win in 1593 ticks, the same in every process. The headless game has no UI root, so the seat stands a `Targeter` in for `Find.Targeter`, which the permit call goes through.

The compound has a bed behind the back wall (`DefenseStage`): the rescue order carries a downed colonist to one and is refused `no_bed` without.
