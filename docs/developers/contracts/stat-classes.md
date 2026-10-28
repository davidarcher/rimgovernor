# Stat evaluation

[Subsystem contracts](README.md)

The brain reads game-computed def-level stats, allowed stuffs and adjusted costs from the load's def stat table,
`DefStatTable` (`brain/src/RimGovernor.Brain.Bridge/Catalog/DefStatTable.cs`), through the `DefinitionCatalog`
members in `Catalog/Stats.cs`. The table is keyed by the game's Def objects; the catalog maps the names its callers
hold to its own Def rows.

## What the table holds

- Every ThingDef with no stuff and with each stuff `GenStuff.AllowedStuffsFor` allows it, and every TerrainDef.
- For each subject, every stat `CatalogStats.All` declares. A value is computed when the stat's `ShouldShowFor`
  holds for the subject (`StatRequest.For(def, stuff)`, Normal quality), and for every subject when the stat is
  declared `ReadHidden` (including MarketValue, DeteriorationRate, Flammability, BedRestEffectiveness, WorkToBuild). Construction-cost valuation reads native material MarketValue even when its display is hidden, as with quest ingredients; display/trade reads still check `Shown`. Missing or failed values remain errors, never zero. The value is the game's
  `GetStatValueAbstract(stat, stuff)`.
- Each subject's `CostListAdjusted(stuff, errorOnNullStuff: false)`; a def made from stuff has no cost row without one.
- Each def's allowed stuff names.

`CatalogStat` has no public constructor, so a stat the brain reads must be declared in `CatalogStats`; that list is
exactly what the table computes. Reading a stat whether shown or not (`PlannerStatValue`, `TerrainWorkToBuild`) when
it is not declared `ReadHidden` is a `CatalogException`.

## Who builds it and when

The mod's `NativeStatTable` builds it in one game-thread pass inside the definition catalog read
(`NativeDefinitionCatalogTool.HandleReadDefinitionCatalog`), the first time a load's catalog is read, and logs
`[RimGovernor] Def stat table: … in N ms`. `NativeCatalogFactory.Create` takes the table that read published for the
same load token. The table is immutable after the pass, so the brain reads it on any thread with no game hop and no
wait. The creation-time ideoligion choice reads no stats and uses `DefStatTable.Empty`.

The values are taken as fixed for a load: stat parts read the scenario and difficulty of the loaded game, so a new
load rebuilds the table. Stats that depend on a live thing or pawn are not in the table: the brain reads them from
the snapshot frame (pawn, room and building rows).

## Failures

There is never a guessed default. A stat the game does not define, a subject outside the table (for example a stuff
the def does not allow), and a value the game could not compute (an exception or a non-finite number, kept as that
failure) are a `StatException` naming it. There is no per-class owner table: the game evaluates every `StatWorker`
and `StatPart` itself, so no class is re-implemented or tracked in the repository.

## Planning values

A planning definition has total common values and a named availability:
NotInCatalog, ResearchLocked, or Ready. A name outside the catalog is not an
offered option. Catalog or research reads that fail throw; they do not produce an
empty catalog or an unavailable approximation.

Plant, harvest, diet, floor, power, explosive and grower values live in
nullable component records. A missing component describes a definition that has
no such native component. Readers select the relevant component before reading
its required values. A fixed construction recipe holds its costs and build work;
a definition made from stuff holds the allowed stuff recipes and their computed
stats instead. Empty costs mean the native recipe is free.

Every colony frame carries the edible crop diet and demand rows, alongside the
native zone cell and unroofed cell counts. The common colony decoder combines
these rows with the load catalog and finished research, so direct colony reads
and review reads use the same complete crop choices. Growth and harvest work
consume plain values; an unsown field is fallow and a field with no future growing
window is dormant. Neither is an unread growth rate.
