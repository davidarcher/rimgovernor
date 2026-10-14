# Pawn activity observations

The native pawn activity tracker is passive. It never assigns or cancels work.
It starts with the in-process brain and writes to that host's existing OTLP sink.
Only spawned, living colonists are observed; other pawns and off-map time are
outside this window. Missing history is not idle time.

Job starts and ends are captured at native lifecycle hooks. The end prefix reads
the old job before cleanup can replace or recycle it. Every 15 game ticks, the
tracker samples movement, carrying, job driver/toil index and supported native
phase evidence. Activity and purpose are separate. Walking while carrying is
carrying; stationary unsupported phases stay unknown. A vanilla active-skill
phase is classified as work, not proof of useful output. Sleeping/eating/joy,
explicit Wait, combat and downed recovery have separate categories. A modded
job retains its raw identifiers; standing still never implies waiting for inputs.

Phase boundaries have up to 15 ticks of sampling uncertainty and short phases
can be missed. Job hooks carry exact observed ticks, but this does not make the
internal phase estimates exact. Each interval records its sampling resolution.
It includes raw job ID/definition, phase, targets, locations and end condition.
Purpose comes from existing operation intent or the native work type. Current
native actions do not carry Method or scheduler-assignment IDs; those remain
null rather than being guessed from purpose text. Later assignment contracts
can supply them without introducing another activity history.

Only open intervals live in memory (cap 512 pawns). Long intervals checkpoint
after 2048 game ticks; this bounds the unflushed tail and is a telemetry choice,
not a game constant. A capacity event reports refused observations. The existing
OTLP queue and rotating logs own retention (default 16 x 32 MiB logs, queue 16384);
there is no new file, save component or database. Ring rotation, queue loss,
crashes and an unfinished interval appear as missing time in a requested report.
Rewind/load closes old intervals only through their last observation and starts
a new world/load/map identity. Death/despawn closes at the next sampled census;
its boundary has the same sampling uncertainty. Observation exceptions stop the
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

Pure tests cover interruption, replacement, unknown jobs, bounds, missing records
and timeline changes. observations/pawn-activity starts from a small prepared lab,
walks a colonist to a mature crop, verifies travel/work intervals, actual produced
items and a worker-attributed output event. A snapshot cannot exercise native
Harmony hooks or establish actual delivery. The case reports paired steady-state
sampling/accounting timings on the same paused fixture. Those microbenchmarks
exclude asynchronous disk export and do not certify whole-game TPS or cost on
large colonies; no low-overhead claim follows from compilation alone.

The case also benchmarks a disabled sink versus real OTLP enqueue/encoding/flush on the same captured fixture interval in the harness process. Its separate benchmark ring is test evidence and is never merged into colony history.
