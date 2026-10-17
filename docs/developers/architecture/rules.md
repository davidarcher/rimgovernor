# Architecture rules

Six rules are mechanical gates, one more gate (the world-state ban) bans game APIs from the
brain, the record-orphan job of the repo gate catches unread contract, Policy and fixture-contract record members
(see [Record orphans](#record-orphans)), the acceptance-case gates hold the cases to their charter (see
[Acceptance cases](#acceptance-cases)), and three are review questions. The C# brain enforces the rules as Roslyn
analyzers in `brain/analyzers/RimGovernor.Brain.Analyzers`, loaded into every
`brain/src` project by `brain/Directory.Build.props`, so they run inside every
`dotnet build`, the gate's among them (the tests projects are not gated). Each gate
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
come from the def stat table. Baseline (`worldstate.txt`) `file|Type.Member|Namespace.Type[.Member]`;
`worldstate.txt` starts empty. The game-thread side (the def stat table pass and the
native mod) is not brain code and is not analyzed.

Construction tiers (`RGARCH008`, no baseline): every call of
`DomainAction.NewBuildingAction` states its tier as a `ConstructionTier`
member or through a Policy `RoomTier`/`PlannerTier`/`AdoptedTier`/
`DefenseBuildTier` call. The only pass-through caller is the store codec's
`ReadBuilding` (a row without a tier does not load); see
[construction tiers](../contracts/construction-tiers.md).

### Record orphans

This is a gate job (`contract orphans` in `RimGovernor.cmd test`,
`tools/repo/Commands/ContractOrphans.cs`): every member of a public wire type in
`RimGovernor.Brain.Contracts` (enums aside) and of a record in `RimGovernor.Brain.Policy` must be
read somewhere outside its declaration, and a member the native mod writes must be read by the brain.
An analyzer sees one project at a time, and IDE0051/IDE0052 only private members, so this
reads the built assemblies instead: every other `brain/src` assembly (tests excluded) and the native
mod's own `RimGovernor.*` assemblies, by the member tokens the compiler bound (getter calls,
initializer setters, positional constructors, property patterns), never by name. A reference into the
declaring assembly is followed through its calls to the backing fields they reach, so a computed
property reads what it computes from and a Policy function reads the records it decides over.
Constructing a type reaches its virtual methods (overrides, interface implementations, iterator
bodies). Record equality, hashing, printing and cloning, and the JSON context and converters, touch
every member and count as nothing: a member that only takes part in equality is an orphan. A parameterless
constructor's field initializers are not a write by whoever constructs the type. Findings
read `Contracts|Type.Member` or `Policy|Type.Member` with a reason (`not referenced outside its
declaration`, `written, never read`, `written by the native side, never read by the brain`). The baseline
`brain/analyzers/baseline/orphans.txt` holds the orphans that predate the check (burndown: [#3351](https://github.com/davidarcher/rimgovernor/issues/3351)), and it only shrinks: a new
orphan fails (delete it through every layer, never add a line), and so does a line that is no
longer an orphan (delete the line). The wire and Policy checks are unavailable without the game, because then the
native mod is not built and the game-reading brain projects are empty.

The public types of `RimGovernor.Fixtures.Contracts` (the stage spec and its reply, enums aside) are checked
the same way, as `Fixtures|Type.Member`. Their other end is the fixture build's `test/stage`, which the gate
never builds, so the acceptance cases and their harness are the readers: a case setting a spec member uses
it as a case reading a reply member does. This check needs no game, and it has no baseline lines.

Acceptance cases read many native replies by JSON key (`WireCodec`'s flat view, `JsonNode`
lookups such as `view["shown"]`), which no symbol shows. As a stopgap until the cases read typed
Contracts records (a migration that will be its own epic), the check counts as read every wire
member whose JSON name (taken from `WireJsonContext`'s own metadata, so naming policy and
`[JsonPropertyName]` apply) equals an identifier-shaped string literal anywhere in the
`RimGovernor.Brain.Acceptance*` assemblies. This is deliberately loose: no field a case reads by key
is flagged, at the cost that a common key such as `"label"` keeps a real orphan alive. A new or
touched case reads the typed record instead (`WireCodec.ToRecord<T>` and the member), so the read
is compile-checked and visible to the gate. PlayerApi also serves Presentation records as JSON to
HTTP clients ([player API](../contracts/player-api.md)); a field that contract documents has a
reader the check cannot see.

### Acceptance cases

Two gates hold the cases to the [acceptance charter](../testing/acceptance-guide.md#charter) without a game:

- **Catalog test** (`AcceptanceCatalogTests` in `RimGovernor.Brain.Domain.Tests`). It builds the whole registry
  through `CaseCatalog.Register`. Every typed case must declare a non-blank `Scope`, `Ticks` within its start's
  cap and a defined `Tier`. Every legacy case must pass the runner's `AcceptanceCase.Lint()`. The legacy case
  count must equal the test's `LegacyCases` constant, which only shrinks: a port or deletion lowers it in the same
  commit, and a new legacy case fails.
- **Banned APIs (RS0030)** on `RimGovernor.Brain.Acceptance.Cases`: `Microsoft.CodeAnalysis.BannedApiAnalyzers`
  over the project's `BannedSymbols.txt` (`Task.Delay`, `Thread.Sleep`, the `JsonNode`/`JsonObject` indexers).
  The ban is per project, not per case type, so the project's `.editorconfig` makes it an error and keeps a
  shrink-only baseline of legacy files, one `[/Area/File.cs]` section each, at `suggestion`. The compiler still
  writes their uses to `banned-api.sarif`, and the gate's `banned-API baseline` job fails a section whose file
  no longer uses a banned API (delete the section). Never add a section; write the case on the typed API.

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
