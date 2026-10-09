# Architecture rules

Five rules are mechanical gates, three are review questions (#2497, epic
#1856). The gates live in `go/internal/archgate`, run inside `go run ./cmd/test`
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
   `maxResourceTarget` or delete the cap), and no sentinel standing for
   unknown (`1e12`, `1<<40`, `math.MaxFloat64`). A cap that triggers is a
   visible refusal with a reason, never a silent drop. Baseline: `file|func|token`.

## Review items

The landing report answers each in a line.

6. **Orchestrators sequence, they do not compute.** Math on domain quantities
   is a policy function tested without a runtime.
7. **Resource demand is one pipeline.** Detectors and planners read the same
   value (scope of #2494). The sole exception is a produce bill's ingredient
   need between the decide round and the bill being placed: the resource
   planner chooses it, so trade, workshop and acquisition do not see it and may
   sell the ingredient meanwhile (accepted; #2500, revisited under #2504).
8. **Unknown is never a number.** Use `domain.Known`/unknown, not a sentinel,
   a zero or a clamp.

Deferred: file and function size ratchets, table-row registries (planner
catalog), comment-staleness grep.
