# Architecture rules

Six rules are mechanical gates; three are review questions. The gates live in
`go/internal/archgate`, run inside `go run ./cmd/test`
(as an ordinary Go test) and compare against checked-in baselines in
`go/internal/archgate/baseline/rule<n>.txt`. A baseline may only shrink: a new
violation fails, and so does a baseline entry that no longer occurs (delete
it). Never add a line to make a gate pass. Every gate failure starts
`arch rule <n>`.

## Gates

1. **Layers.** `domain -> policy (pure) -> bridge -> facts -> observation ->
   store -> snapshot -> executor -> buildingruntime -> httpapi -> cmd`. A
   package may import only packages at or below its own layer (subpackages
   belong to their parent). Bridge validates the wire, observation decodes,
   policy decides, store persists, buildingruntime sequences. Role denials on
   top of the order: bridge does not import policy, store does not import
   bridge or gabp, executor does not import store, httpapi does not import
   buildingruntime. Baseline: those edges as they exist today.
2. **Policy is pure.** Non-test files in `internal/policy` import none of
   `time`, `sync`, `sync/atomic`, `runtime`, `os`, `net*` and start no
   goroutines. Search budgets count iterations, never wall time. Baseline:
   `clock_window_types.go` (`time.Time` fields).
3. **One owner per exported type.** An exported type name declared in two or
   more of `domain`, `policy`, `bridge`, `observation` fails unless listed.
   Baseline: today's duplicates.
4. **No swallowed errors.** No `if err != nil` with an empty body anywhere in
   `internal` or `cmd`. Baseline: `file|func` entries.
5. **Numbers (narrow).** In `policy` and `buildingruntime`: no literal `10000`
   compared with or clamping a count/target-named operand (use
   a named constant or delete the cap), and no sentinel standing for
   unknown (`1e12`, `1<<40`, `math.MaxFloat64`). A cap that triggers is a
   visible refusal with a reason, never a silent drop. Baseline: `file|func|token`.

6. **Game data is read, not retyped.** In non-test Go outside
   `internal/nativeaccept`: no quoted thing or terrain def name (a static field
   of the game's `ThingDefOf` or `TerrainDefOf`, listed in the generated
   `archgate/defof_names.txt`) and no literal equal to a mirrored `GameConstants`
   value (an integer of at least 1000 that is not a power of ten or two,
   generated into `archgate/game_constants.txt`). Read the fact from the catalog,
   or register a judgment table in `policy.DefTables` (a package-level var of a
   registered table is an allowed owner, as is `domain/game_time.go`). Baseline:
   `file|func|"DefName"` or `file|func|number`; this rule's cap is above 200
   because it baselines every literal that predates it. The def-name list is
   refreshed with `defmirror --report` (see `contracts/schema-generation.md`);
   the constants list with `RG_UPDATE_GOLDEN=1 go test ./internal/archgate -run
   TestGameConstantsListIsFresh`.

## Review items

The landing report answers each in a line.

7. **Orchestrators sequence, they do not compute.** Math on domain quantities
   is a policy function tested without a runtime.
8. **Resource demand is one pipeline.** Detectors and planners read the same
   value from the shared demand calculation. Produce-bill ingredients remain
   planner-local between decision and placement; trade, workshop and acquisition
   can therefore sell or consume them in that interval. This remains an explicit
   exception; per-target construction finishing settings do not reserve ingredients.
9. **Unknown is never a number.** Use `domain.Known`/unknown, not a sentinel,
   a zero or a clamp.
