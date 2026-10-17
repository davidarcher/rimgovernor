# Choose checks for a change

[Developer guide](../README.md) · [Workflow](../development-process.md) ·
[Acceptance guide](acceptance-guide.md)

## Testing budget and evidence reuse

Run `RimGovernor.cmd test > test.out 2>&1` from the worktree root once per milestone.
It runs the [gate](../development-process.md#project-wide-build) when anything it builds
changed (`RimGovernor.cmd affected` says whether a change owes it). Wait for the final
PASS/FAIL; do not pipe it or follow it with your own build or test.

Use `-full` at an epic's end or when changed code is covered by a skipped test; it runs
the gate even when the tree matches the base.
A clean merge of main does not invalidate a passing result. After resolving
conflicts, build the touched projects instead of rerunning tests.

Native acceptance is not a landing gate. The nightly validates gameplay on
main; run a targeted case earlier when it is needed to establish a native claim.
Before a slow check, name that claim and why a cheaper check cannot prove it.

## Available checks

| Changed behavior | Cheapest useful evidence |
| --- | --- |
| Planner choice, ranking, admission or recovery decision | A bug-driven xUnit test in `brain/tests`, or a [recorded colony snapshot](colony-snapshots.md) replayed through `RimGovernor.Brain.Snapshot`. |
| Supply behavior across days, shocks and colony sizes | `RimGovernor.Brain.Simulation` (seeded offline stock-flow scenarios). |
| Boundary decoding, journal or uncertain-write handling | Real decoder/storage tests and fault injection through typed fakes. |
| Concurrency | A focused test that reproduces the race; repeat only to reproduce the relevant flake. Whole-module race/stress belongs to the nightly. |
| C# or wire records | `RimGovernor.cmd test` (the gate); the wire records are the only schema, so nothing is regenerated. |
| Native operation, read contract or vanilla simulation | Registered `acceptance run <area>/<case>` with an observed native postcondition. |
| Colony autonomy over time | Nightly campaign with native end-state and Concern progress, and no post-setup harness interventions. |
| Documentation | Check commands, links and source claims; `RimGovernor.cmd test` selects checks by affected paths. |

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

### Reused evidence

Every acceptance run executes setup, behavior and assertions in that attempt;
keep `result.json` provenance with a reported verdict. Reuse evidence when relevant source,
inputs and environment have not changed; record skipped coverage in the commit.

### Deadlines, ordering and latency

Wall-clock deadlines guard hangs. Use game ticks for simulated progress and
synchronized events or virtual time for ordering. A test timeout under machine
load is not a latency measurement. Use the accepted shared guard (fixture `CallTimeout`, or the hang guard the
runner derives from a case's tick budget) rather than tightening per-test deadlines or trimming behavior to fit.

A load-sensitive failure that passes alone does not justify another suite.
Record it in the commit and let the nightly supply broader evidence.

For performance, use [measure throughput](measure-throughput.md).
Native snapshot capture work iterates with `acceptance profile-capture` on
a retained paused colony, reporting p50/p90/max by family. Do not substitute a
long colony run for a capture benchmark.

## Remote checks

[Remote handoff](remote-handoff.md) covers selection, dispatch, diagnostics and
import. A remote result proves its named selection; retain its provenance and
do not infer omitted coverage. What a cloud session can and cannot run is in the
[runbook](../agent-runbook.md#remote-agents).

## Adding a case

Follow the [acceptance charter](acceptance-guide.md#charter) and
[writing a case](acceptance-guide.md#writing-a-case).
