# Architecture rules

Seven rules are mechanical gates (1 to 6 and 9), one more gate (the world-state ban) bans game APIs from the
brain, the record-orphan job of the repo gate catches unread contract, Policy and fixture-contract record members
(see [Record orphans](#record-orphans)), the acceptance-case gates hold the cases to their charter (see
[Acceptance cases](#acceptance-cases)), and three are review questions (rule 9 is both). The C# brain enforces the rules as Roslyn
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
entries (450 for rule 6, 1381 for rule 9) fails in `RimGovernor.Brain.Domain`: stop and review
the rule.

## Gates

1. **Layers (`RGARCH001`).** `Domain -> Contracts -> Policy -> Bridge
   (Bridge.Contracts, Bridge) -> Observation -> Store ->
   Executor -> BuildingRuntime -> PlayerApi -> Host`. The project references
   already form this order and Roslyn cannot compile a cycle; the analyzer
   reads the assembly references of each compilation and fails a reference that
   climbs the order or is a role denial: store does not reference bridge
   or `HttpChannel`; executor does not reference store (it still does, baselined);
   `PlayerApi` does not reference `BuildingRuntime`. `Telemetry`, `Simulation`,
   `HttpChannel` and `Acceptance*` sit outside the order.
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
   catalog; `Domain/GameTime.cs` is exempt. The def names in `Policy`'s judgment
   tables have no exemption: they are baselined as legacy (#3589). Attribute arguments (`[WireName("...")]`) are not
   checked. Both lists are embedded in the analyzer. Brain code that references
   the game assemblies can use the `*DefOf` statics and `GameConstants` directly,
   which is the fix for a Bridge-side hit; `Policy` and `Domain` cannot reference
   the game and keep their judgment tables. Baseline `file|Type.Member|"DefName"`
   or `file|Type.Member|number`; this rule's cap is above 200 because it
   baselines every literal that predates it. The def-name and constants lists are
   edited by hand in the analyzer's `data/` (`defof_names.txt`, `game_constants.txt`) when the game's
   defs change; no generator exists since the def mirror was removed.

9. **There is no unknown (`RGARCH009`).** The gate half of review item 9 ([#3753](https://github.com/davidarcher/rimgovernor/issues/3753)).
   In every `RimGovernor.Brain.*` project except `Acceptance*` it counts these tokens per file:
   - `Fact`: a name bound to `Domain`'s `Fact<T>` (the type, or a member other than `ValueOrDefault`) or to `Fact.Known`;
   - `Unknown`: a call of `Fact.Unknown<T>()`;
   - `ValueOrDefault`: `Fact<T>.ValueOrDefault`;
   - `UnknownMember`: an enum member named `Unknown` or `Unspecified`;
   - `NullableValue` (in `Policy`, `Domain` and `Contracts` only): a property or positional parameter of a
     record or class whose type is a nullable value type (`int?`, `Cell?`);
   - `Discard`: a catch-all `_ =>` arm, `default:` or `case _:` label in a switch whose governing type is
     an enum or an abstract or sealed record declared in the repo. Switches over game or BCL enums, strings
     and tuples are not counted; neither is a guarded `_ when ...` arm.

   The baseline (`rule9.txt`) is counted, unlike the other gates: each line is `Project/File.cs|token|count`.
   A count above its line fails, a file or token without a line fails on its first hit, and a count below its
   line fails too, asking for the line to be lowered, so the baseline always holds what the code holds. A
   deleted file's lines go with it. The starting baseline was generated from the analyzer's own output; its
   cap is that starting line count. The native mod's Bridge (`integrations/rimgovernor-native`) is not analyzed:
   the native build stages only the sources it ships, and its locked restore records project references, so a
   reference to the analyzer that exists in one build and not the other breaks restore. Its unknowns go with
   the family children and its nullable checking (#3791).

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

Live Method stages (`RGARCH010`, no baseline): internal generation and binding
dependencies follow the [ColonyForecast stage contract](colony-forecast.md#live-activity-policy-stages).
Generation cannot construct activity orders; binding cannot generate or compare.
Lifecycle entry points use `CompareActivityOutcomes`; independent ranking,
direct order construction and fabricated forecast selections are rejected.
This gate covers the explicit live Method stages,
not every existing planner or every possible form of decision math.

### Record orphans

This is a gate job (`contract orphans` in `RimGovernor.cmd test`,
`tools/repo/Commands/ContractOrphans.cs`): every member of a public wire type in
`RimGovernor.Brain.Contracts` (enums aside) and of a record in `RimGovernor.Brain.Policy` must be
read somewhere outside its declaration, and an observation member the native mod writes must be read by the brain.
The check is direction-aware by sub-namespace: `Wire.Operations` records are brain-to-native intent (the brain
writes, the native side reads, and the native side may build its own copy to compare against, which is not a
brain-read obligation); every other wire record is a native-to-brain observation. The list is
`IntentNamespaces` on `ContractOrphans.Wire`.
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
`brain/analyzers/baseline/orphans.txt` holds the orphans that predate the check, and the members the check
exposed when it stopped counting acceptance string keys as reads (#3410; burndown for both:
[#3351](https://github.com/davidarcher/rimgovernor/issues/3351)). It only shrinks: a new
orphan fails (delete it through every layer, never add a line), and so does a line that is no
longer an orphan (delete the line). In any epic, a Policy member whose only reader is a later child issue of the same epic may be baselined; the commit body lists it, and the reading issue deletes it. The epic's final issue leaves none (for example the harm model epic [#3471](https://github.com/davidarcher/rimgovernor/issues/3471): readers in #3539 to #3547). The baseline's `Operations.CombatOrderOrder.*`,
`Operations.CombatAnimalAreaArea.Clear*` and `Operations.AbilityIntentTarget.*` lines stay on purpose: the combat
shapes are unread only while combat is away, and its return will read them. The wire and Policy checks are unavailable without the game, because then the
native mod is not built and the game-reading brain projects are empty.

The public types of `RimGovernor.Fixtures.Contracts` (the stage spec and its reply, enums aside) are checked
the same way, as `Fixtures|Type.Member`. Their other end is the fixture build's `test/stage`, which the gate
never builds, so the acceptance cases and their harness are the readers: a case setting a spec member uses
it as a case reading a reply member does. This check needs no game, and it has no baseline lines.

Acceptance cases and their harness read native replies as typed Contracts records
(`Harness.CallAsync` with a `NativeOps` op), so every read is a symbol the check sees. The check counts no
string key, JSON name or by-name read as a read: a member the acceptance code mentions only by its JSON name
is an orphan. PlayerApi
also serves Presentation records as JSON to HTTP clients ([player API](../contracts/player-api.md));
a field that contract documents has a reader the check cannot see.

### Acceptance cases

Two gates hold the cases to the [acceptance charter](../testing/acceptance-guide.md#charter) without a game:

- **Catalog test** (`AcceptanceCatalogTests` in `RimGovernor.Brain.Domain.Tests`). It builds the whole registry
  through `CaseCatalog.Register`. Every case must declare a non-blank `Scope`, `Ticks` within its start's
  cap and a defined `Tier`.
- **Banned APIs (RS0030)** on every `RimGovernor.Brain.Acceptance*` project, harness and cases:
  `Microsoft.CodeAnalysis.BannedApiAnalyzers` over `brain/analyzers/BannedSymbols.Acceptance.txt` (`Task.Delay`,
  `Thread.Sleep`, the `JsonNode`/`JsonObject` indexers). `brain/Directory.Build.props` adds the analyzer and makes
  RS0030 an error in every file of those projects. The compiler writes any use a suppression kept from failing the
  build to each project's `banned-api.sarif`, and the gate's `banned-API uses` job fails it.

## Refreshing

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
9. **There is no unknown.** A planner input is a read value or a named state
   (a union case or an enum member with a meaning, such as `Unreachable` or
   `Never`); a fact that cannot be read raises `RequiredFactException`. Never a
   default, a zero, a sentinel or a clamp: read the fact, name the state, or
   drop the option from planning and file an issue
   ([#3753](https://github.com/davidarcher/rimgovernor/issues/3753)). `Fact<T>`
   is being burned down family by family; gate 9 counts what is left and only lets it fall.
