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
Every enabled review writes `routine-<tick>-<seq>.json` there, `<seq>`
counting from 1 so several reviews at one paused tick each keep a file;
a failed write is
a `[routine] colony snapshot not recorded` service-log line, never a review
error. The acceptance harness passes its environment to the serves it
launches, so from `go/`:

```bash
RIMGOVERNOR_SNAPSHOT_DIR=<abs dir> go run ./internal/nativeaccept/cmd/acceptance run clean/filthy -output <fresh dir>
```

records every review of that case. A checkpoint save is recorded the same
way: resume or `acceptance dev` the case from the bundle, or serve the save
by hand, with the variable set. Pick the tick that shows the decision under
test and copy it into the consuming package's `testdata/` with
`go run ./internal/snapshot/cmd/trim <recording> testdata/<name>.json.gz`,
naming what it shows (`clean-filthy-kitchen.json.gz`); name the case and
commit it came from in the test's comment. Trim writes gzipped compact
JSON without the site cells (tens of KB instead of megabytes);
`-keep-cells` keeps them for a test that runs a site search.

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

Planners whose decisions read the colony at step time, not from the
review's facts, record separately (#745, #746): with the recording
variable set, each step writes `planner-<goal>-<tick>-<seq>.json`
(`snapshot.Planner`) holding the policy inputs it noted: shelter starter
searches, dig search, native excavation site reads by purpose and
dig-or-shell choices; chunk dump sites; animal feed method inputs;
secure-supplies items, hauler candidates and covered storage searches;
shrine defender squads and breach readiness requests. Several steps at
one paused tick each keep their own file. Load it with `snapshot.LoadPlanner` and call the
policy function on the recorded request. Under the acceptance harness the
recording directory gets `<area>/<case>` appended per case. Commit
recordings gzipped (`*.json.gz`); `Load` and `LoadPlanner` gunzip them.

## Planner step reads

The building and bill planners decide from their own colony read at step
time, which carries what the review's read lacks: rooms, the step's own
definitions (every policy lamp for lighting), fresh benches. With the
recording variable set, each such step also writes
`step-<building|bill>-<goal>-<tick>-<seq>.json` (`snapshot.Step`, #794):
the projection it read, Facts included. `trim` recognises the `step-`
prefix and drops the site cells unless `-keep-cells` (a lighting or
placement test needs them). A test loads it with `loadStep` in
`internal/buildingruntime/routine_snapshot_test.go` and calls the
selector on `step.Projection`, taking the policy and latches from the
review recording of the same run (`loadRecorded`, `recordedPlanner`).

## Defense snapshots

A threat response is not a routine review: `RoutineDefensePlanner` reads
the emergency census, the combat pawn rows and (for a hostile building)
the lines of fire itself. With the recording variable set, every defense
step that read the emergency census also writes
`defense-<tick>-<reason>.json` (`snapshot.Defense`): those replies
(protobuf ones as protojson), the stored layout record and the step's
reason and admitted method. `replayDefense` in `internal/buildingruntime`
serves them, re-addressed to the fixture world, to a real planner over a
fresh journal; several files replay in order on one journal, so a hold
and its breach fallback replay as steps (#744).

The defensive layout planner records its three policy decisions as
`layout-<point>-<tick>.json` (`snapshot.Layout`): `propose` (the
`DefenseRequest` `policy.DefenseLayouts` verified the chokepoint with),
`turrets` (the request and geometry `policy.DefenseTurrets` probed) and
`cover` (the request, layout record and site cells
`policy.DefenseApproachesFor` ranked cover over). Tests call the policy
function on the recorded request.
