# Stat worker and part table

`go/cmd/stataudit/stat_classes.tsv` lists every concrete `StatWorker` subclass (26) and
`StatPart` subclass (71) of the installed game. The root `StatWorker`, the default
evaluator of a `StatDef` with no `workerClass`, and abstract classes are not rows.
It exists so the stat evaluator's port of game-computed stat math is tracked class by
class: the goal is that every row reaches an owner.

## Columns

- `class`, `kind` (`worker` or `part`).
- `category`, from the decompiled body and its base classes:
  `data` (reads only its own fields, which the mirror carries), `state` (reads pawn,
  room, map or thing state), `difficulty_gear` (reads difficulty, storyteller or
  gear), `other` (no source). The classifier is regex-based and conservative: a body
  that mentions `req.Thing` is `state`.
- `inputs`: the state tokens the body reads (`room`, `map`, `temperature`, `time`,
  `pawn_health`, `pawn_skills`, `pawn_needs`, `pawn_story`, `pawn_other`, `thing`,
  `apparel`, `equipment`, `difficulty`), or `none`.
- `defs`: the StatDefs using the class through `workerClass` or `parts`, including
  those inherited through `ParentName`, across all installed packs.
- `hash`: digest of the whitespace-normalised decompiled class plus its base classes
  below `StatWorker`/`StatPart`.
- `owner`: `unowned` or `stateval.<Class>`. Hand-maintained; regeneration keeps the
  owner of every class that remains. No other per-class bookkeeping.

## Commands (from `go/`)

- `go run ./cmd/stataudit` regenerates the table from the installed game.
- `go run ./cmd/stataudit --check` writes nothing and fails when a class is new, gone,
  or its hash differs from the checked-in table, meaning the game's code changed since
  the table was written. Regenerate, then review the owner of each changed class.

Both decompile `Assembly-CSharp.dll` with the global `ilspycmd` tool
(`dotnet tool install -g ilspycmd`); nothing in the repository pins it, the same
convention as `cmd/thoughtaudit` ([mood control](mood-control.md#thought-trigger-table)).
`-decompiled <dir>` reuses an existing decompilation.

## Not mirrored

`bridge.NotMirrored{Class, Fact}` is the typed error for a fact the game computes in
code that the mirror lacks. The stat evaluator returns it for a class whose owner is
`unowned`; it never substitutes a default.

## The Go evaluator

`go/internal/bridge/stateval` is the Go port of `StatWorker.GetValue` and `ShouldShowFor`
for definition requests (a ThingDef or TerrainDef with optional stuff and quality). Its
core is the base `StatWorker`: `statBases` or the `StatDef` default, the stuff factor and
offset, the `StatPart` list in descending priority, the post-process curve, the scenario
factor, rounding and the min/max clamp, in `float32` as the game does. Pawn and thing
requests carry state the rows lack and are not evaluated.

- A `StatPart` registers in `stateval.ownedParts` and its row's owner becomes
  `stateval.<Class>`; a `StatWorker` subclass is `NotMirrored` for both value and
  `ShouldShowFor` until a later change ports it. `TestOwnerColumnMatchesEvaluator` fails
  when the owner column and the registry disagree.
- The environment is explicit (`stateval.Env`): the active mods (`ModsOf` reads them off
  the catalog's rows), classic mode and the scenario's stat factors. A missing mod set is
  an error, not "no mods".
- `TestParityWithRecordedStatTable` compares the evaluator with every row of the recorded
  `DefStatTable`, bit for bit, and `testdata/not_mirrored_stats.txt` lists the stats that
  still fail with `NotMirrored` and the class each names (`go test -update` rewrites it; a
  stat leaves the list only by porting its classes). The native case
  `stats/evaluator-parity` repeats the comparison against the live `EvaluateStat`.
