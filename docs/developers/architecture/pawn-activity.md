# Pawn activity observations

The native pawn activity tracker is passive. It never assigns or cancels work.
It starts with the in-process brain and writes to that host's existing OTLP sink.
Only spawned, living colonists are observed; other pawns and off-map time are
outside this window. Missing history is not idle time.

Job starts and ends are captured at native lifecycle hooks. The end prefix reads
the old job before cleanup can replace or recycle it. Shared native toil
transitions, path start/stop, carry operations, downing/recovery and pawn
spawn/despawn/faction/death changes update the observation at their game tick.
There is no tick hook or periodic colonist scan. Attaching to a running game
performs one initial census.

Activity and purpose are separate. An active path is travel (or carrying),
including temporary congestion along the route. This measures travel time, not
per-cell motion. Stationary unsupported phases stay unknown. A vanilla active-skill
phase is classified as work, not proof of useful output. Sleeping/eating/joy,
explicit Wait, combat and downed recovery have separate categories. A modded
job retains its raw identifiers; standing still never implies waiting for inputs.

Hook boundaries use the transition's game tick. Nested instant toils can advance
several times in that tick; zero-duration intermediate states do not charge time.
The record describes the live state after the callback. Mods that bypass native
transition methods or mutate semantic state within a toil are not comprehensively
observed; exact timestamps do not imply comprehensive semantic classification.
Intervals include raw job ID/definition, phase, targets, locations and end condition.
Purpose comes from existing operation intent or the native work type. Current
native actions do not carry Method or scheduler-assignment IDs; those remain
null rather than being guessed from purpose text. Later assignment contracts
can supply them without introducing another activity history.

Only open intervals live in memory (cap 512 pawns). Quiet tasks need no heartbeat
or periodic checkpoint. A capacity event reports refused observations. The existing
OTLP queue and rotating logs own retention (default 16 x 32 MiB logs, queue 16384);
there is no new file, save component or database. A live interval is exported when
its state changes or ends. Until then its tail appears as unknown in offline
reports; a crash can lose that entire open interval. Ring rotation and queue loss
also appear as missing time. Absence of an event during a long task is not itself
a gap. Rewind/load resets at the next lifecycle observation, closes old intervals
only through their last observation, and starts a new world/load/map identity.
Death/despawn closes through lifecycle hooks. Observation exceptions stop the
tracker with a fault event; they do not interfere with pawn jobs.
## Reader

Use the world and pawn identifiers from pawn_activity_interval rows:

```text
RimGovernor.cmd pawnactivity -logs <absolute logs.jsonl> -world <colony/load/map> -pawn <pawn ID> -from <start tick> -to <end tick>
```

The JSON report groups elapsed game ticks by activity, purpose, job and outcome,
with interval drill-down and explicit missing-record rows. Choose one game-day
window for a pawn-day report. It rejects overlapping history rather than charging
the same time twice. Open tails remain unknown until an interval closes. Raw
phase, target and attribution evidence remains available in the source log.

Existing delivery hooks also emit pawn_activity_output when they observe produced
units. Crop/forage/fishing include the worker; animal outputs without a known
worker leave it null. These are production-site outputs, not storage receipts.
Successful job termination is never used as a substitute for physical output.

## Evidence

Pure tests cover long event-free tasks, same-tick transitions, interruption, replacement, unknown jobs, bounds, missing records
and timeline changes. observations/pawn-activity stages a grower, one mature potato in a growing zone twelve cells away and
two colonists with no work, lets vanilla work selection run, and requires travel and work intervals and a worker-attributed
output event. A snapshot cannot exercise native Harmony hooks or establish actual delivery. No case measures the
tracker's cost; no low-overhead claim follows from compilation alone.
