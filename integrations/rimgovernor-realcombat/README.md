# Real combat host

RimWorld's own combat code, run headless. A hand-ported combat sim would re-implement game logic that can drift
from every game update; this host runs the game's logic itself, in a bare `net10.0` process (Waylith has no Unity), so there is nothing to
port. Tracking issue: #3142. Nothing tunes policy at runtime, so the engine is offline only: one game per process,
no live colony control. Its optional prediction service is described below.

## Bounded prediction worker

`RealCombat.Host --game <private game directory> --worker stdio` boots once,
emits a content-identified ready frame, then accepts strict schema-3 requests as
JSON lines. Stdout carries protocol frames; game logs go to stderr. One request
runs at a time. Frames are at most 1 MiB, one request has at most 64 seeds/32 actors,
128 cells per side and 60000 ticks. The ready frame announces `arenaBudget`, the
requests one process answers (eight): every request disposes its native map and
settlement, and the budget limits other retained process state. The owner
(`LocalEncounterPredictor`) counts and replaces the process before the budget;
the worker does not refuse for it. The budget does not hide earlier
reconstruction failures. A failed arena ends the worker; an unsupported request
returns an explicit rejection. No automatic retries, fallback or colony orders.

The Host library's `LocalEncounterPredictor` supplies tighter caller budgets,
cancellation and owned-process cleanup behind the implementation-independent
`IEncounterPredictor`. The [owning contract](../../docs/developers/contracts/encounter-prediction.md)
defines provenance, stale-result fencing, supported observations and qualification.
The worker returns research samples, not qualified hunting decisions. Verify
runs each seed twice and is included in effort/runtime accounting.

`RealCombat.NativeSmoke --game <private game> --predictor <absolute host DLL>
--fixture <absolute fixture directory> --out <absolute JSON>` exercises the same
committed fixture seed through the direct engine and cold/warm worker, then
unsupported input and cancellation. This is a lifecycle/reconstruction probe,
not a statistical calibration sweep or a live hunting acceptance case.

| Project | What it is |
|---|---|
| `src/RealCombat` | Library. `GameHost.Boot` loads every def, the language and a seeded world; `Arena` builds a map, sweeps it and fights it once per seed. `Protocol.cs` is the request/response shape. `skips.txt` lists the UI, audio and non-combat tickers Harmony skips so the tick runs without a window. |
| `src/RealCombat.Host` | The process: `RimGovernor.RealCombat.Host --game <dir> --request <json> --out <json>`. |
| `src/RealCombat.Native` | Library. `DefenseStage` raises a ring-walled compound and the planner's killbox in it. `NativeSeat` hosts the native mod's own `CombatMirror` (observation) and `NativeCombatOrders` (orders) over an arena map, so a controller sees and commands a simulated fight through the code it uses against the live game. Bridge exposes its internals to it (`InternalsVisibleTo`). `Arena.Controller` is the hook that plays the colonists instead of the built-in retargeting (the hostiles then act under an assault lord, as a raid does). `PolicyController` is the controller that decides with the live policy. |
| `src/RealCombat.NativeSmoke` | Throwaway: a duel observed and commanded through the seat, or with `--policy` a 2v1 played by the live combat policy (`PolicyController`) inside a staged killbox compound (`DefenseStage`). |

## The request format

`RealFightRequest` is the one request format, strict and versioned. `Schema` must equal `RealFightRequest.CurrentSchema` (3);
a request that omits it or states another version is rejected. **Unsupported input rejects, it is never defaulted or
regenerated.** The whole request is rejected before the arena is built (so no map and no pawn exists), and every problem
names its field (`Pawns[0].Hediffs[1].Part: Human has no body part 'tail'`). A rejection exits the host with code 3 and no
result file. Rejected: a JSON member the format lacks; an unknown def name (kind, weapon, apparel, skill, trait and its
degree, xenotype, hediff, terrain, weather, faction, ability, title, permit); a weapon that is not a weapon or apparel that
is not apparel; a cell outside the map; a duplicate or empty pawn id; a hediff or missing part on a body part the race lacks
(parts are named by `BodyPartRecord` label, "left arm"; a label that names two parts is rejected as ambiguous), a hediff on
a part that is missing or inside one, an injury without a part, a severity outside `(0, maxSeverity]`; a humanlike pawn
without `Age` or `Xenotype`; an animal or insect with xenotype, gear, skills or traits. Positive observed animal ages are reconstructed and read back.

**Wild prey and hunters.** `Side = "wild"` is a faction-less animal under the game's own animal AI: nothing scripts it, so it
flees or retaliates as its race's rules say (`Pawn_MindState.Notify_DamageTaken`), and herd mates react to damage to one
another as in the game (`GetPackmates`: same race, same faction, within 24 cells, same district; the game needs no lord
for this, so the arena builds none). `Herd` is a string id on wild animals only, two or more members of one kind (a lone
animal states none); the game groups by race and proximity, so two herds of one race close together react as one.
`Hunt` on a humanlike colonist names the wild pawn it hunts, driven by the game's Hunt job (`JobDriver_Hunt`, ordered with
`ignoreDesignations` so the request stands in for the Hunt designation, and re-ordered each time it ends while the prey is
up). Rejected by field name: a wild pawn that is not an animal, `Herd` on anything else, a herd of one or of mixed kinds,
`Hunt` on a non-colonist or naming no wild pawn. The fight ends when the colonists are out or no hostile and no wild
pawn stands (wild means not downed or dead: prey that fled and lives runs to `MaxTicks`); `Winner` stays
colonist/hostile, prey that down or kill the hunters count as hostile. `--hunt` is the smoke test (a hunter, a deer
herd and a boar herd, Verify, an injured prey that must read back).

`Departures` (pawn `Id`, `Tick`, `Kind`) replay timed exits. At `Tick` (0 to `MaxTicks`; applied before that tick runs) a
`leave` takes the pawn off the map at once (the game's `ExitMap`); a `flee` undrafts it and runs it (a goto job) to the nearest
reachable edge cell, then exits it. From then on it is neither target nor shooter and the side-win check ignores it. An unknown
id, a repeated id, a tick outside `0-MaxTicks` or another kind rejects by field (`Departures[0].Tick`). Per pawn the result
reports `DepartureTick` (-1 when none applied, e.g. the pawn was already down), `GotAway` (off the map alive) and `LastShotTick`
(no shot after the departure). Each fight restarts the map's snow depth from the baseline, so later seeds and `Verify` replays
start from the same ground. The baseline is bare: snow the generator laid is cleared, as in the native lab (snow slows pathing).

**The result** carries the same `Schema` as the request (the shape changes with the version). Per pawn it exports game facts
and no derived scores, read from the game objects after the fight: `Injuries` (every hediff but missing parts: def, part
label, `Severity`, `BleedRate`, `TendableNow`, `Tended`, `Permanent`), `Missing` (part labels), `BleedRate` (the
game's `BleedRateTotal`) and `NeedsTend` (`HasHediffsNeedingTend`); `Kills` (ids of the pawns it killed, by the death's
instigator, in death order; a bleed-out has no killer) and, for a dead pawn, `Products` (race meat and leather defs with
the pawn's `MeatAmount` and `LeatherAmount` stats, before butcher efficiency and rounding); and event ticks from the
fight start: `FirstAttackTick` (a shot or a melee attack; `FirstShotTick` stays ranged only), `DepartureTick` (a replayed
departure applied) and `WithdrawalTick`/`Withdrew` (the tick the pawn left the map alive by any cause, a replayed
departure or the animal AI's own flight to the edge; -1 and false otherwise). `Verify` compares every exported value.
`--hunt` checks the outcome against a direct read of the game objects.

A pawn's `Hediffs` (def, part, severity) and `Missing` (part labels) are its whole health: nothing the generator rolled
survives. `Age` (biological and chronological) and `Xenotype` are required for humanlike pawns.

After the arena is built, before the first tick, the host reads every imported field back from the game objects (map size,
terrain, weather, hostile faction, standing things, and per pawn side, kind, cell, age, xenotype, weapon, worn, stated
skills, traits, carried items, hediffs, missing parts, psycasts, title, favor, permits) and compares it with the request. A
mismatch fails the run by field name (`readback Pawns[0].Age: requested ...`). `RealFightResult.Readback` carries what
the game holds. A pawn's inventory is stripped of what the generator rolled (a raider's medicine), so `Carried` is
checked for equality.

Gear and ground are stated, not rolled. A weapon (`WeaponQuality`, `WeaponHitPoints`) and each `Worn` item (`Quality`,
`HitPoints`) carry a QualityCategory name (`Awful` to `Legendary`; empty keeps the rolled quality) and current hit points
(0 is full condition; a count above the item's maximum, which stuff and quality set, fails the readback). `Terrain` is the
base fill and `Ground` lays rectangular regions over it, each with a `Terrain` and/or a `Roof` (`RoofConstructed`,
`RoofRockThin`, `RoofRockThick`; later regions win, every cell starts unroofed). The readback compares every cell with the
request and reports cell counts per terrain and roof in `TerrainCells` and `RoofCells`. Things (cover, obstacles) are
unchanged.

## Supported domain and the reconstruction gate (#3156)

The gate: a request states an observed encounter, the game builds it, and the readback equals what was stated with
nothing defaulted or ignored. `fixtures/` holds one observed hunting scenario that passes it:
`hunting-observed.request.json` (two hurt hunters with graded gear, a wild boar herd close by and a deer herd far off,
sand over soil, a constructed and a thin-rock roof patch, one hunter leaving at tick 400) and the expected
`hunting-observed.readback.json` (what the game held: the stated values plus the resolved weapon and apparel hit points
and per-terrain and per-roof cell counts). `hunting-unsupported.request.json` is the same request with one member the
format lacks (`tameAfterHunt`); it must reject naming it. `--fixture fixtures` runs both in one process (one arena, one
seed, `Verify` on) and exits 0 when the readback equals the committed file value for value; `--record` rewrites the
readback file (review it against the request before committing). **A passing smoke proves the round trip. It is not a
calibration claim, and the single seed says nothing about outcome rates.**

Supported scenario classes: a map up to the configured size with a base terrain, rectangular terrain and roof regions,
and standing things; colonist and hostile humanlikes and animals/insects with stated age, xenotype, skills, traits,
weapon and apparel (quality, hit points), carried items, hediffs, missing parts, psycasts and Empire title; wild
faction-less animals in herds under the game's animal AI; colonists hunting a named wild animal; timed departures.
Unsupported (rejects, never approximated): anything in the rejection list above, plus everything the format has no
field for (time of day, changing environment, food and rest needs, mood, bonds, mental breaks, dynamic events, other maps).

Result shape: per seed `Winner`, `Ticks` and per pawn the facts under "The result" above; `Readback` once per request.

Known caveats, kept honest:

- **Withdrawal** is leaving the map alive by any cause: `WithdrawalTick`/`Withdrew`. `GotAway` is only the flag for a
  replayed departure. In the fixture the leaving hunter reads `Withdrew` true at tick 400 and `GotAway` true; prey that
  flees under its own AI would read `Withdrew` without `GotAway`.
- **Boar retaliation is unverified.** The `--hunt` smoke never had the hunter hit, and the fixture's closer-range boars
  (placed 23, 18 and 15 cells from the hunter, the last two with a weaker shot) were killed or wounded without ever hitting a hunter in the one seed
  played, so the fixture does not show a retaliation. The retaliation path is the game's own, not scripted here.
- The hunters do not eat, rest or tend: injuries are read at the fight's end, never healed.

## Arena lifetime and repeatability

`Arena` owns one native map and its settlement. Use `using`; constructing another
arena while one is active is rejected. Disposal runs native map deinitialization,
releases simulation resources, and removes the settlement. Once map generation
has returned, failed reconstruction also releases ownership. A map-generation or
cleanup failure ends the worker; in-process recovery is not provided. The headless host skips only the rendering-section
cleanup that cannot run because rendering sections were never created.

Construction starts at tick zero before selecting and generating the seeded map;
fights retain their existing fixed start tick. Keeping a preceding settlement
occupied changed the next selected tile, snow and temperature. Clearing the map
and settlement, and restoring construction time, removes that cross-request state.
Explicit requested terrain also removes generated temporary terrain first:
RimWorld's `SetTerrain` leaves an ice overlay intact. This is reconstruction,
not suppression of ice changes during a fight.

`RealCombat.NativeSmoke --game <private game> --arena-isolation --fixture
<absolute fixture directory> --out <absolute JSON>` runs four independent arenas
with the committed hunting fixture's single seed. It requires identical cell terrain,
environment, readback and full physical outcomes, including the boar kill and timed
hunter departure. It also checks overlapping ownership, use after disposal, failed
construction cleanup, and reconstruction over injected native ice. Run it in two
fresh processes and compare the JSON bytes for cross-process repeatability.
A fake engine cannot establish native map lifetime or terrain-layer behavior.

This bounded regression covers the reported repeated-arena failure, not statistical
prediction accuracy, revenge calibration, arbitrary mod caches or live-colony
reconstruction. #3129 remains unqualified until its separate gates are met.

## Build and run

The game copy must match the runtime's `Assembly-CSharp` (the private copy under `.rimgovernor/native-rimworld`, whose
`Data` is a junction to Steam). Where `RimWorldDir` has no `Assembly-CSharp.dll` the projects build as stubs.

```bash
dotnet build integrations/rimgovernor-realcombat/src/RealCombat.NativeSmoke -c Release -p:RimWorldDir=D:/rimgovernor/.rimgovernor/native-rimworld
dotnet integrations/rimgovernor-realcombat/src/RealCombat.NativeSmoke/bin/Release/RimGovernor.RealCombat.NativeSmoke.dll --game D:/rimgovernor/.rimgovernor/native-rimworld --policy
```

`--request-check` is the request-format test: it runs every reject path (each must name its field and leave no map or pawn)
and an accept case (age 41, Hussar, a missing hand, injuries on the right arm) that must read back equal and play
deterministically, with a second hunter who leaves at tick 450 (`--flee`: flees instead) and must fire nothing after it;
exit 0 is a pass. `--gear-check` plays a poor, half-hit-point rifle and worn flak vest against new gear
(the fights differ) and reads a two-terrain map with two roofed patches back cell for cell.

`--bare` plays both sides with the built-in retargeting, `--policy` plays the colonists with the live combat policy,
`--drugs` gives each colonist go-juice (`--high` makes the raiders high on it, `--raiders N` adds raiders),
`--trace` prints each decision and its results, `--open` fights on open ground with no layout, `--no-traps` leaves the
planner's spike traps out of the compound so the raider reaches the firing line (otherwise it usually dies to the traps
first; with them out the colonists fire and win in 2406 ticks). `--armor` puts both sides in a flak vest and
helmet, `--melee` gives the raiders longswords, `--cover` lays sandbags in front of the colonists on open ground, `--psycast`
gives the first colonist the Stun psycast and moves the raider within the policy's 20 cell casting reach (`--policy --open --psycast`: colonist win, 734 ticks); `--permit` is described under Reads; `--hive` (with `--policy --open`) is the infestation described under Reads; `--doortest` (with `--policy --open --hive`) probes door mechanics, see Known harness facts.

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
`IGameApi` that serves only the catalog and the frame stream; any other call throws by name) and
reads it with the brain's own `GameClient.ReadCombatAsync`. `CombatReads.DecideCombatStep` then runs the
defense round's chain (frame inputs, `CombatView`, the newest stop, `DecideCombat`) and builds the wire batch, which
`NativeSeat.Apply` hands to the order handler. It decides at the first check, at each stop event and at a 300-tick backstop.

Geometry asks are answered from the arena map through the mod's own `NativeCombatGeometryTools`. The defense layout comes
from `DefenseStage`, which stages a compound as the colony's planner expects to find one: a ring wall across the map but
for the killbox's opening (`OpeningPlan`, `LayoutKillbox`), a census of every cell in the killbox region read off the map,
and `DefenseLayout.DefenseLayouts` over it. Every building the planner proposes (funnel and back walls, the defenders'
door, spike traps, fences, sandbags) is placed at once, so a fight starts on the finished defense, and the firing line,
retreat cells, direction and choke the policy holds are read off the result the way the defense round reads them
(`PolicyController.Layout`). `--open` withholds all of that: open ground, no layout, the squad fallback.

The policy fields two defenders per opponent, so a 1v1 duel is
never one of its fights.

## Determinism

A seed fully determines a fight, within and across processes. The game's `Rand` is counter based, so pushing a seed
fixes every draw, provided nothing else moves between fights: thing ids and the tick counter restart from fixed
values, the world and map are generated under fixed seeds, the map is swept (things, roofs) and settled before the
baseline, the fight's things are destroyed afterwards and the world pawns the generator parked are removed. Snow
(it melts on the tick and sets movement cost) and the weather's age are restored to the arena's values before every
fight.
`"verify": true` in a request plays every seed twice and flags a mismatch. A 1000-seed run is byte-identical across two
processes. About 8–12 ms a fight after a ~7 s startup.

## Known harness facts

- Doors (`--doortest`): a drafted pawn ordered onto a door cell opens the door as it approaches (the engine's approach notification, before it steps in); a Stop issued then leaves the pawn on the cell it was on and the door open. The door stays open `ticksUntilClose` = 210 ticks, then shuts. `HoldOpen` only keeps an already-open door from shutting; it does not open a closed one. Clearing the hold lets it shut on its own. `Building_Door.StartManualCloseBy(pawn)` on an open, empty door sets the timer to 110 ticks and the door shuts when it runs out (it is not instant).
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
no other order, and the handler applies it (one colonist is then overdosed and downed, the other rescues them to the bed and the fight stands off without a winner at 3000 ticks). **Infestation**: `--hive` starts one: `HiveStage` spawns a hive under thick rock roof at 44,30 with three player stone doors in series west of it (the door probe's ground), with the hive's own pawn spawning off, three staged megascarabs near it, and three riflemen west of the corridor. The infestation tactic fights them directly with guns (the colonists win in 929 ticks). An outmatched infestation waits and evacuates (`Outmatched`).
The harness had to change for the throw to land at all: a projectile launches from the caster's draw position, which only the render pass
advances, so a pawn that had walked shot from the cell it spawned in; `Arena.Play` now resets each pawn's tweened position every tick
(this moved the numbers above: `--no-traps` 2406, `--permit` 1372). **Royalty**: built as the rounds build it, from the frame's colony royalty section and pawn rows against the catalog
(`ColonyPlanningReads.RoyaltyOf`), so it is known where Royalty is loaded. `RealPawn.Psycasts` gives a colonist a psylink, the
named psycasts and full psyfocus. The step's cast and permit orders leave the batch as Ability actions (`CombatStep.Calls`), and
`NativeSeat.ApplyAbility` runs them through the mod's `NativeAbilityOperations`. They go after the batch: a combat order issued after a cast replaces the cast job. The caster is not left idle: it stands in `Wait_Combat` and fires at will (same shot count as its partner). `RealPawn.Title`, `Favor` and `Permits` make an Empire title holder; with `--permit` the first colonist is an acolyte with the trooper squad permit against four raiders, the policy calls the aid at the first stop (outmatched) and the colonists win in 1372 ticks, the same in every process. The four Empire troopers land beside the caster about 330 ticks in and march to the raiders; `--permit --no-aid` withholds the permit and the raiders win at 919 ticks. The headless game has no UI root, so the seat stands a `Targeter` in for `Find.Targeter`, which the permit call goes through.

The compound has a bed behind the back wall (`DefenseStage`): the rescue order carries a downed colonist to one and is refused `no_bed` without.


Observed hunting requests may supply fixed outdoor temperature and sky illumination.
These are reconstructed and read back; omitted offline environment remains explicitly
outside qualification. Every sample and Verify replay now disposes and regenerates
its native map, while definitions and the worker stay warm. Clearing pawns and snow
alone failed the independent single-hunter replay. Preparation uses its own RNG stream; future sampling begins only after reconstruction. Reserved prediction-owned pawn IDs keep native hash-interval ticks stable when warm generation consumes a different number of temporary gear IDs. No live IDs or future RNG enter this process. Hunt actors are undrafted; calm
herd bystanders do not extend the encounter after its target and actual retaliators
are out. A prey's observed map exit ends its participation without crediting products.
See the [independent CombatLab protocol](../../docs/developers/testing/hunting-prediction-qualification.md)
for source/predictor provenance, calibration and retained failures.
