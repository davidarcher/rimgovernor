# Choose checks for a change

[Developer guide](../README.md) · [Workflow](../development-process.md) ·
[Acceptance guide](acceptance-guide.md)

## Testing budget and evidence reuse

Run `go run ./cmd/test > ../test.out 2>&1` from `go/` once per milestone.
It formats changed Go files, runs vet/staticcheck and `go test -short ./...`,
and builds affected probes. The Go cache reuses unchanged package results.
Wait for the final PASS/FAIL; do not pipe it or follow it with a full Go suite.

Use `-full` at an epic's end or when changed code is covered by a skipped test.
`-full` always checks the whole Go module, even when the tree matches the
base. Use `-full -base <revision>` to also check formatting across an epic;
formatter file arguments are batched below the Windows command-line limit.
A clean merge of main does not invalidate a passing result. After resolving
conflicts, build and vet the touched Go packages instead of rerunning tests.

Native acceptance is not a landing gate. The nightly validates gameplay on
main; run a targeted case earlier when it is needed to establish a native claim.
Before a slow check, name that claim and why a cheaper check cannot prove it.

## Available checks

| Changed behavior | Cheapest useful evidence |
| --- | --- |
| Planner choice, ranking, admission or recovery decision | Go unit test or [recorded colony snapshot](colony-snapshots.md). |
| Supply behavior across days, shocks and colony sizes | `internal/supplysim` and buildingruntime's food/resource matrices; use full tests when changing a slow scenario. Baselines only shrink. |
| Boundary decoding, journal or uncertain-write handling | Real decoder/storage tests and fault injection through typed fakes. |
| Concurrency | Focused `go test -race -run <tests>`; repeat only to reproduce the relevant flake. Whole-module race/stress belongs to the nightly. |
| C# or shared protobuf | Root `task build` and `task test`; regenerate both languages for schema changes. |
| Native operation, read contract or vanilla simulation | Registered `acceptance run <area>/<case>` with an observed native postcondition. |
| Colony autonomy over time | Nightly campaign with native end-state and Concern progress, and no post-setup harness interventions. |
| Documentation | Check commands, links and source claims; `cmd/test` selects checks by affected paths. |

`acceptance list` is authoritative for registered cases. Follow the
[runbook](../agent-runbook.md) for private game setup and the
[acceptance guide](acceptance-guide.md) for authoring and running cases.
The [shelter coverage map](shelter-coverage.md) identifies which checks own
specific shelter claims.

## What a result proves

Compilation proves wiring, a decoder test proves interpretation, a snapshot
proves a decision on its recorded facts, and a native case proves only its
asserted effects. An accepted order is not completed pawn work. Measure native
postconditions for construction, production, recovery and combat outcomes.

### Full, cached and resumed runs

Keep `result.json` provenance with a reported verdict:

| Execution | Claim |
| --- | --- |
| Fresh run | Setup, behavior and assertions ran in this attempt. |
| Cached precondition (`staged_from`) | Behavior ran from a retained stage; setup was reused. |
| Resumed suffix (`resumed_from`) | Only the suffix after the checkpoint ran. |
| `-postmortem-only` | Reads/assertions ran over a retained outcome. |
| `acceptance dev` | Edited code ran from a retained bundle. |

Do not present cached, resumed or postmortem evidence as a fresh full run.
The ordinary suite uses fresh execution. Reuse evidence when relevant source,
inputs and environment have not changed; record skipped coverage in the commit.

### Deadlines, ordering and latency

Wall-clock deadlines guard hangs. Use game ticks for simulated progress and
synchronized events or virtual time for ordering. A test timeout under machine
load is not a latency measurement. Use the accepted shared guard (bridge
`testBudget`, fixture `CallTimeout`, or the 60-second `checktesttimes` guard)
rather than tightening per-test deadlines or trimming behavior to fit.

A load-sensitive failure that passes alone does not justify another suite.
Record it in the commit and let the nightly supply broader evidence.

For performance, use [measure throughput](measure-throughput.md).
Native snapshot capture work iterates with `acceptance profile-capture` on
a retained paused colony, reporting p50/p90/max by family. Do not substitute a
long colony run for a capture benchmark.

## Remote checks

[Remote handoff](remote-handoff.md) covers selection, dispatch, diagnostics and
import. A remote result proves its named selection; retain its provenance and
do not infer omitted coverage. Game-free Linux compilation is in the
[runbook](../agent-runbook.md#remote-agents).

## Adding a case

Use the [acceptance authoring guide](acceptance-guide.md#adding-a-case).
A planner-decision failure belongs in a snapshot test; replace and deregister
a native case whose only claim is a planner choice.
