# Colony snapshots

A colony snapshot is the input one enabled routine review planned against,
recorded from a native run and replayed in `go test` (package
`internal/snapshot`). It turns a question a native case answers in minutes
("does the review open `MaintainCleanFacilities` on this colony?") into a
millisecond unit test over the facts that colony really produced.

## What a snapshot holds

`snapshot.Routine`, one JSON file:

- `Facts`: the `policy.RoutineFacts` the review passed
  `policy.DetectRoutine`, after the journal's enrichment (runways, claims,
  recovered comfort and sleep, filtered salvage). Every census a planner
  of that tick reads rides in it: the colony grid, upkeep rooms and
  clearance, research, resources, work profiles.
- `Latches` and `Policy`: the prior latches and the staged policy the
  review detected against, so replay reproduces hysteresis and stage
  budgets.
- `Review`: the journal's routine review cursor after the review filed
  (goal bindings, progress records, stage, dependencies).
- `Projection`: the colony reading the review took, holding what the
  planners read beyond `Facts`: site `Cells`, planning `Definitions`,
  `PowerPlanning`, `Rooms`, `Bounds` (its zone read and window are not
  recorded). `Load` restores its `Facts` from `Facts`.
- `Recorded`, `Snapshot`, `Tick`: provenance.

`domain.Fact` values are carried exactly: a known fact is `{"v": value}`,
an unknown one is absent or `null`. Fields are named by their Go names and
zero fields are left out. A field the code no longer has fails the load
with `re-record the snapshot`.

## Recording

Set `RIMGOVERNOR_SNAPSHOT_DIR` to an absolute directory for any serve.
Every enabled review writes `routine-<tick>.json` there; a failed write is
a `[routine] colony snapshot not recorded` service-log line, never a review
error. The acceptance harness passes its environment to the serves it
launches, so from `go/`:

```bash
RIMGOVERNOR_SNAPSHOT_DIR=<abs dir> go run ./internal/nativeaccept/cmd/acceptance run clean/filthy -output <fresh dir>
```

records every review of that case. A checkpoint save is recorded the same
way: resume or `acceptance dev` the case from the bundle, or serve the save
by hand, with the variable set. Pick the tick that shows the decision under
test, gzip that file into the consuming package's `testdata/` (`Load`
reads `.json.gz`; a recording with its projection runs to megabytes) with a name
saying what it shows (`clean-filthy-kitchen.json`), and name the case and
commit it came from in the test's comment.

Re-record when a load fails on a renamed or removed field, or when the
recorded facts no longer describe what the review now reads. Rerun the
same case and replace the file; keep the assertions, since they state the
behaviour, not the recording.

## Replaying

```go
r, err := snapshot.Load("testdata/clean-filthy-kitchen.json")
needs, err := r.Detect()                                  // policy.DetectRoutine over the recording
a, err := r.Assessment(policy.MaintainCleanFacilities)   // one goal's assessment
```

A planner is a policy function over the same facts: call it with
`r.Facts` (and `r.Policy`), or a building planner's `select*` with
`*r.Projection` (`internal/buildingruntime/routine_snapshot_test.go`),
and assert the chosen method, target or refusal.
Tests edit the loaded facts to probe a variant of the recorded colony
instead of hand-building a whole fixture.

A snapshot proves decisions over recorded facts, never native behaviour:
the facts are whatever the native read returned at that tick.

## Planner steps

The shelter planner's own decisions read the colony at step time, not
from the review's facts, so they record separately (#745): with the
recording variable set, each shelter planner step writes
`planner-<goal>-<tick>.json` (`snapshot.Planner`) holding its starter
searches, dig search, native excavation site reads by purpose and
dig-or-shell choices. Load it with `snapshot.LoadPlanner` and call the
policy function on the recorded request. Under the acceptance harness the
recording directory gets `<area>/<case>` appended per case. Commit
recordings gzipped (`*.json.gz`); `Load` and `LoadPlanner` gunzip them.
