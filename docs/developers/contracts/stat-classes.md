# Stat evaluation

[Subsystem contracts](README.md)

The brain reads game-computed stats through `IStatSource` (`brain/src/RimGovernor.Brain.Domain/Stats/IStatSource.cs`):
the game's `StatWorker.GetValue` and `ShouldShowFor` for a `ThingDef` or `TerrainDef` with optional stuff and
quality, plus the cost and stuff queries the catalog derivations need. The production implementation is the
game-thread stat service (#3080); tests and the simulation use `FakeStatSource`.

A def, stuff or stat the source does not know, and a stat class it cannot evaluate, throw `StatException`; there is
never a guessed default. There is no per-class owner table: the game evaluates every `StatWorker` and `StatPart`
itself, so no class is re-implemented or tracked in the repository.
