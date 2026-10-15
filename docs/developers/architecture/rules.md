# Architecture rules

Six rules are mechanical gates, one more gate (the world-state ban) bans game APIs from the
brain, and three are review questions. The C# brain enforces them as Roslyn
analyzers in `brain/analyzers/RimGovernor.Brain.Analyzers`, loaded into every
`brain/src` project by `brain/Directory.Build.props`, so they run inside every
`dotnet build` and `task build` (the tests projects are not gated). Each gate
compares its findings with a checked-in baseline in
`brain/analyzers/baseline/rule<n>.txt` (an `AdditionalFiles` input; `worldstate.txt` for the ban). A baseline
may only shrink: a new violation is a build error, and so is a baseline entry
that no longer occurs (delete it). Never add a line to make a gate pass. Every
gate error reads `arch rule <n> ...` with the diagnostic id `RGARCH00<n>`.

A baseline entry belongs to one project (its first path segment, for example
`Policy/Foo.cs|...`), and a project only reports entries of its own. A project
built `unavailable` (no game in `RimWorldDir`, see `brain/Game.props`) compiles
as an empty assembly and is skipped, so on a machine without the game its
baseline entries are neither checked nor reported stale. A baseline over 200
entries (450 for rule 6) fails in `RimGovernor.Brain.Domain`: stop and review
the rule.

## Gates

1. **Layers (`RGARCH001`).** `Domain -> Contracts -> Policy -> Bridge
   (Bridge.Contracts, Bridge) -> Observation -> Store -> Snapshot ->
   Executor -> BuildingRuntime -> PlayerApi -> Host`. The project references
   already form this order and Roslyn cannot compile a cycle; the analyzer
   reads the assembly references of each compilation and fails a reference that
   climbs the order or is a role denial: store does not reference bridge,
   `HttpChannel` or `NativeFacade`; executor does not reference store;
   `PlayerApi` does not reference `BuildingRuntime`. `Telemetry`, `Simulation`,
   `HttpChannel`, `NativeFacade` and `Acceptance*` sit outside the order.
   Bridge validates the wire, observation decodes, policy decides, store
   persists, buildingruntime sequences. Bridge returns Policy types by design, so
   no "bridge does not import policy" edge is denied.
   Baseline `From->To`: the denied edges as they exist today.
2. **Policy is pure (`RGARCH002`).** `RimGovernor.Brain.Policy` uses no
   `System.IO` or `System.Net` file or device API, `DateTime`, `DateTimeOffset`,
   `TimeProvider`, `TimeZoneInfo`, `Stopwatch`, `Process`, timer, `Thread.Sleep`,
   `Task.Delay` or `Environment` member. The in-memory text types (`Stream`,
   `StreamReader`, `StringReader`, `StringWriter`, `MemoryStream`, `Path`) stay
   legal, because an embedded resource is read through them (an embedded resource), and
   so does `Environment.ProcessorCount`. `TimeSpan` is a value and stays legal.
   Search budgets count iterations, never wall time. Concurrency is allowed only
   while the result stays deterministic: each worker writes only its own slot
   and results merge in index order, so neither scheduling nor worker count can
   change the output (`EachIndex` in `LayoutSiteGround.cs`). The gate cannot
   check that; it is a review question. Baseline `file|ban Type`:
   `ClockTypes.cs` (`DateTimeOffset` fields).
3. **One owner per exported type (`RGARCH003`).** A public type name declared in
   two or more of `Domain`, `Policy`, `Bridge.Contracts`/`Bridge` and
   `Observation` fails unless listed. These four form one reference chain, so
   the higher project sees the collision and reports it. Baseline
   `Higher|Name`: today's duplicates.
4. **No swallowed errors (`RGARCH004`).** No `catch` block without a
   statement in any `brain/src` project. A comment does not count as handling.
   Baseline `file|Type.Member`. Many entries are a skipped `DomainException` or best-effort cleanup in the
   acceptance harness; review them as you touch the file and delete the line
   once the catch records or rethrows.
5. **Numbers (narrow, `RGARCH005`).** In `Policy` and `BuildingRuntime`: no
   literal `10000` compared with or clamping (`Math.Min`/`Max`/`Clamp`) a
   count/target-named operand (use a named constant or delete the cap), and no
   sentinel standing for unknown (`1e12`, `1L << 40`, `double.MaxValue`). A cap
   that triggers is a visible refusal with a reason, never a silent drop.
   Baseline `file|Type.Member|token`.
6. **Game data is read, not retyped (`RGARCH006`).** In every `brain/src`
   project except `Acceptance*`: no quoted thing or terrain def name (a static
   field of the game's `ThingDefOf` or `TerrainDefOf`, listed in
   `analyzers/RimGovernor.Brain.Analyzers/data/defof_names.txt`) and no literal
   equal to a mirrored `GameConstants` value (an integer of at least 1000 that is
   not a power of ten or two, `data/game_constants.txt`). Read the fact from the
   catalog, or register a judgment table in `DefTables()` (a declaration or type
   whose name `DefTables()` mentions is an allowed owner in `Policy`, as is
   `Domain/GameTime.cs`). Attribute arguments (`[WireName("...")]`) are not
   checked. Both lists are embedded in the analyzer. Brain code that references
   the game assemblies can use the `*DefOf` statics and `GameConstants` directly,
   which is the fix for a Bridge-side hit; `Policy` and `Domain` cannot reference
   the game and keep their judgment tables. Baseline `file|Type.Member|"DefName"`
   or `file|Type.Member|number`; this rule's cap is above 200 because it
   baselines every literal that predates it. The def-name and constants lists are
   edited by hand in the analyzer's `data/` (`defof_names.txt`, `game_constants.txt`) when the game's
   defs change; no generator exists since the def mirror was removed.

**World-state ban (`RGARCH007`)**: no world-state game API on the planner worker thread. The
whole brain runs off the game thread, so a `brain/src` project that
references the game assemblies may read immutable Def data and nothing that
reads or holds live world state. Banned: static classes of the game
(`Find`, `Current`, `Rand`, `GenTicks` members other than constants, `GenDate`,
the `Gen*` helpers, `StatExtension`, `Scribe`...), live objects (`Thing` and
every subclass such as `Pawn` and `Building`, `Map`, `Game`, `TickManager`,
`World`/`WorldObject` and the rest of `RimWorld.Planet`, `Faction`, the
`Pawn_*` trackers, `Verse.AI` classes such as `Job` and `Lord`, `Hediff`,
`Need`, `Room`, `Precept`, `StatWorker`...), and the main-thread classes of
Unity. Allowed (the Def-data allowlist): `Def` classes and their members,
`DefDatabase<T>`, the `*DefOf` statics, constants and static fields of plain
values (the game's tuning numbers), enums, structs, delegates, interfaces,
attributes, `typeof(X)` and generic type arguments (class tokens that a Def's
`thingClass` is compared with), non-live helper classes that are not on the
banned lists (`GraphicData`, `StatModifier`, `CompProperties`), and the
members in `WorldStateAnalyzer.PureMembers` (`GenCollection.Any`). World state
reaches the brain only as facts decoded from the wire, and computed stats
come from `IStatSource`. Baseline (`worldstate.txt`) `file|Type.Member|Namespace.Type[.Member]`;
`worldstate.txt` starts empty. The game-thread side (the stat service, #3080, and the
native mod) is not brain code and is not analyzed.

Construction tiers (`RGARCH008`, no baseline): every call of
`DomainAction.NewBuildingAction` states its tier as a `ConstructionTier`
member or through a Policy `RoomTier`/`PlannerTier`/`AdoptedTier`/
`DefenseBuildTier` call. The only pass-through caller is the store codec's
`ReadBuilding` (a row without a tier does not load); see
[construction tiers](../contracts/construction-tiers.md).

## Refreshing

Live Method stages (`RGARCH009`, no baseline): internal generation and binding
dependencies follow the [ColonyForecast stage contract](colony-forecast.md#live-activity-policy-stages).
Generation cannot construct activity orders; binding cannot generate or compare.
Lifecycle entry points use `CompareActivityOutcomes`; independent ranking,
direct order construction and fabricated forecast selections are rejected.
This gate covers the explicit live Method stages,
not every existing planner or every possible form of decision math.

A violation you fixed leaves a stale baseline line, and the build says which:
delete it. To see all findings of a project while fixing, build it; the
diagnostics list every finding with its baseline key, so a key can be copied into
the right file only for a violation that predates the gate (the baselines were
seeded that way), never for new code. Changing an analyzer changes how a key is
spelled: regenerate by building with the descriptors temporarily at `Warning`
severity and writing the reported keys, then review the diff.

## Review items

The landing report answers each in a line.

7. **Orchestrators sequence, they do not compute.** Math on domain quantities
   is a policy function tested without a runtime.
8. **Resource demand is one pipeline.** Detectors and planners read the same
   value from the shared demand calculation. Committed fixed production batches
   publish exact single-choice ingredients on the next review; shared acquisition,
   trade retention and stone-wall funding consume them ([scope](colony-forecast.md#live-fixed-batch-material-promises)).
   Fixed batches additionally check the same budget functions inside Method commit;
   journal actions own claims between reviews. Pending building placements and food activities now share this boundary ([live work resources](colony-forecast.md#shared-live-work-resources)). Terrain work, indefinite production and
   trade/export paths retain the planner-local exception. Per-target construction
   finishing settings do not reserve ingredients.
9. **Unknown is never a number.** Use `Fact<T>`/unknown, not a sentinel,
   a zero or a clamp.
