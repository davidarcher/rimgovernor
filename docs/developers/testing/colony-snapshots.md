# Colony snapshots

A colony snapshot is the input one enabled rounds planned against: the facts a
review detected over, so a decision can be replayed over what a real colony
produced instead of a hand-built fixture. It proves decisions over recorded
facts, never native behavior.

## Current state

The recorder types live in `brain/src/RimGovernor.Brain.Snapshot`
(`SnapshotRounds`, `SnapshotPlanner`, `SnapshotStep`, `SnapshotDefense`,
`SnapshotLayout`, `SnapshotCombatStop`, `SnapshotFirebreak`, and the background
queue `SnapshotLater`). Writing goes through `ISnapshotSink`
(`SnapshotPackage.Sink`), which the host supplies; none is installed, so nothing
is written today, and the remote workflow and fixture factory do not collect
snapshot streams (the observe SQLite feed and Grafana dashboards were dropped, #3144). There is no
file format, trim or re-record tool, and no committed recording. Recording is
evidence for replay and is never part of a decision.

## What a snapshot holds

- `Facts`: the rounds facts the review passed to detection, after the
  journal's enrichment (runways, claims, recovered comfort and sleep, filtered
  salvage). Every census a planner of that tick reads rides in it.
- `Latches` and `Policy`: the prior latches and the staged policy the review
  detected against, so replay reproduces hysteresis and stage budgets.
- `Review`: the journal's rounds cursor after the review filed.
- `Projection`: the colony reading the review took, holding what planners read
  beyond `Facts` (site cells, planning definitions, power planning, rooms,
  bounds).
- `Recorded`, `Snapshot`, `Tick`: provenance.

Other recordings are written per decision point: planner steps whose decisions
read the colony at step time (`SnapshotPlanner`, `SnapshotStep`), defense steps
(`SnapshotDefense`), the defensive layout planner's `propose`, `turrets` and
`cover` decisions (`SnapshotLayout`) and combat stops (`SnapshotCombatStop`).
A test calls the policy function on the recorded request.

The acceptance harness appends `<area>/<case>` to the recording directory per
case.
