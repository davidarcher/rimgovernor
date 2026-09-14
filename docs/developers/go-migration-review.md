# Go migration review

Review of `codex/g01-05` at `f0f8b58a`, against the Python implementation in the
same checkout. This is a source and retained-evidence audit, not a new test run.
The actionable checklist is exclusively in the
[G01 issues](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AG01%22).

**Superseded by G01.07–G01.12.** The capability table below reflects the state at
the reviewed revision, well before G01.07–G01.10 closed and G01.12 switched the
production default to Go. It is retained as a historical audit, not a current
capability boundary. For the current boundary (including what still runs through
Python — chat, and the `launch.ps1` rollback path) see [the Go module
guide](../../go/README.md).

## Finding

Go is a partial controller with substantial observation, policy and persistence
support. It cannot replace the Python controller today. The principal gap is
turning most decisions into executable work and verifying its outcome. The work
has advanced much further across observation/planning than across execution.

The strongest source evidence is the closed action list in
[`domain/plan.go`](../../go/internal/domain/plan.go): building, owned draft and
melee attack. [`executor.go`](../../go/internal/executor/executor.go) dispatches
these families. This count excludes separate lifecycle/clock/control APIs; it is
not a claim that Go exposes only three API operations.

[`serve_clock.go`](../../go/cmd/rimgovernor/serve_clock.go) wires seven optional
construction planners: sleeping, starter shelter, cooking facility, comfort,
expansion, power and temperature. Sleeping and shelter are alternative compiler
configurations. `--routine-methods` executes their shared building plans.
It does not execute food acquisition, bills, work settings, care, gear or recovery
proposals. A campfire built by Go is not a completed cooking workflow.

The local-model decoder in
[`interpreter/decode.go`](../../go/internal/interpreter/decode.go) accepts only
building proposals. [`launch.ps1`](../../launch.ps1) still invokes Python and
[`containers/Dockerfile`](../../containers/Dockerfile) still builds Python runtime
images. The existence of a Go interpreter or HTTP service is not full chat or
production replacement.

At the reviewed revision, local `main` is `07e7b5df`; the task branch has 46 commits
not on main and main has no commits absent from the task branch. This review does
not merge them. Branch availability and production delivery are separate facts.

## Capability assessment

“Executed” below means the stated bounded scenario, not general colony parity.
“Planning” means observations/needs or proposals exist without an executable
routine path. Absence findings use both the Python compiler branches and Go's
closed action set/service composition, not filenames alone.

| Existing Python workflow | Current Go replacement | Evidence / boundary |
| --- | --- | --- |
| Shared goals, priorities, reservations, dependencies, cancellation | Substantial reusable implementation | `store/routine.go`, `store/goals.go`, `store/goal_admission.go`, executor and runtime; building paths exercise it. General behavior for missing action families is not established. |
| Indoor sleeping and starter shelter | Executed bounded construction | `routine_sleeping.go`, `routine_shelter.go`; shelter acceptance-12 passes. Does not establish full adoption, storage or bed-upgrade parity. |
| Cooking | Facility construction only | `routine_sleeping.go`/cooking tests and service wiring; no bill action in the shared action model. |
| Spare housing | Executed bounded expansion | `routine_expansion.go`; expansion acceptance-03 passes. |
| Temperature | Executed heating/cooling | `routine_temperature.go`; native room temperature recovery, not just construction. |
| Power | Executed generation/conduit cases | `routine_power.go`; consumer network becomes supplied after ordinary pawn work. |
| Dining and recreation | Implemented; partial native evidence | `routine_comfort.go`; acceptance-21 records outcome and Manual, but the run fails. Inspected comfort reports do not establish a complete passing lifecycle scenario. |
| Naming and starting supplies | Detection/history | `routine.go`, `starting_supplies.go`; no routine confirmation/Allow compiler in service wiring. |
| Work allocation | Assignment policy and readback | `policy/work_assignment.go`, routine work projection and saved preferences; no shared pawn-setting action. |
| Food and wood | Forecasts, needs and spatial proposals | `food_forecast.go`, `starter_layout.go`, field budgets; no acquisition, zone, hunting or bill action. |
| Defense and urgent care | Emergency gates; draft/melee machinery | Melee executor exists, but no complete routine squad/medical composition. Missing move/ranged/tend/rescue/equip families. |
| Equipment | Needs, replacement and production proposals | `policy/gear.go`, native gear projection; not connected to execution. |
| Four direct upkeep needs | Native targets and durable needs | Secure supplies, repair, clean and fire census; no haul/repair/clean routine jobs. Python firefighting can deliberately wait for safe ordinary labor. |
| Home coverage and stone walls | Ownership evidence and needs | Exact owned-construction readback; no Home edit or staged replacement composition. |
| Sleeping upkeep | Need and use history | Upgrade/assignment/use observations; floor-place replay does not establish real-bed use or assignment execution. |
| Medical care and reserves | Needs and retained patient/stock history | No care settings, surgery or replenishment execution. |
| Animal containment/feed | Needs and feed hysteresis | No pen/feed method execution. Herd configuration is a separate unported workflow. |
| Mood | Durable causes, relief candidates and clock hold | Native food/mental-break review acceptance; no actual Go relief action. |
| Disaster recovery | Durable history and bounded candidates | Native recovery acceptance-03/replay; no area lease or service job executed. |
| Research and player resource production | No complete routine replacement | Python `research.py`, `production_policy.py`, `extraction_development.py`; no matching routine compiler/action path. Resource spending rules in Go are not production targets. |
| Population, herd and waste policy | No complete routine replacement | Python refresh/compile paths exist; dynamic goals and their actions are absent from current Go routine composition. |
| Trade, caravans, quests and multi-map progression | No complete replacement | Python actions/policies exist; absent from Go's shared action variants. |
| Player commands, media and production lifecycle | Partial infrastructure | Building-only interpreter; existing HTTP/clock/draft controls; Python still launches production. |

## Python specification inspected

The finite behavior reference is
[`colony_skills.py`](../../controller/rimgovernor/colony_skills.py), especially
`compile`, together with
[`colony_controller.py`](../../controller/rimgovernor/colony_controller.py) for
arbitration, renewed deficits, watchdogs and retries. The ten upkeep contracts are
enumerated in [`colony_upkeep.py`](../../controller/rimgovernor/colony_upkeep.py).
Their delegated modules, plus `development.py`, `production_policy.py`,
`research.py`, `medical_management.py`, `population.py`, `husbandry.py`,
`waste_management.py` and `service_recovery.py`, define existing method behavior.

[`colony_plan.py`](../../controller/rimgovernor/colony_plan.py) enumerates Python
actions and native operations; [`player_commands.py`](../../controller/rimgovernor/player_commands.py)
enumerates command variants. These are coverage checklists, not permission to add
new gameplay or to copy the generic unvalidated Python argument boundary into Go.
Shared native tools should be reused where their current contract is sufficient.

## Retained evidence checked

Paths below are under the repository's local `.rimgovernor/` directory and are
generated artifacts, not committed source. Aggregate reports have different
schemas; a missing top-level `scenario_passed` field is not a failed test.

| Artifact | What it supports |
| --- | --- |
| `g01-05-shelter-acceptance-12/result.json` and inner routine report | Both pass; inner report contains shelter/sleeping audit, recovery, outcome and Manual evidence; worker exit zero and cleanup. |
| `g01-05-expansion-acceptance-03/result.json` and inner routine report | Both pass; inner report contains expansion audit, recovery, outcome and Manual evidence; worker exit zero and cleanup. |
| `g01-05-power-methods-verification.json` | Generation/conduit native identities, supplied consumer, accounting and restart evidence. |
| `g01-05-temperature-verification.json` | Cold-02 and hot-05 recovery evidence, Manual/restart and replay; earlier failed trials remain recorded. |
| `g01-05-mood-verification.json` | Native planning and mental-break hold evidence; explicitly excludes relief execution. |
| `g01-05-disaster-verification.json`, `g01-05-recovery-verification.json` | Disaster review and three refuge admission candidates; explicitly excludes recovery execution. |
| `g01-05-comfort-acceptance-21/run/native-go-routine-acceptance/result.json` | Partial outcome and Manual evidence; overall failure, not acceptance completion. |
| `g01-05-comfort-acceptance-22/run/native-go-routine-acceptance/result.json` | Failed on interruption; does not close the complete lifecycle gap. |

## Process assessment

The previous backlog mixed completed implementation history, narrow acceptance
claims and unfinished work in one G01.05 entry. It then described many of the same
workflows again under G01.07. “Finish planning, then actions” encouraged broad
observation coverage without making the controller usable. Splitting a workflow
across those IDs is an ownership label, not two independent deliverables.

Typed boundaries, one shared executor, cancellation, resource accounting and
outcome verification are necessary foundations. Repeatedly expanding durable
planning records and running live fixtures before connecting their actions did
not close the main execution gap. Schema and native changes must be justified by
an actual consumer in the capability being delivered.

There is no defensible completion percentage or time estimate from commit counts,
lines of code, scenario directory counts or this audit. Full capability-level
timing and coverage data were not reconstructed. The source proves a substantial
remaining port, not merely final packaging work. The backlog now defines complete
workflow deliverables and preserves the original Python-free production endpoint.
