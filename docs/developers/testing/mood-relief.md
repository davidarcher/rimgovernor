# Verify mood relief in Docker

[Documentation](../../README.md) · [Mood contracts](../contracts/mood-control.md)

## Go planning acceptance

Use `scripts/container_scenario.py` with fresh private inputs, a task-specific
image/output directory and the compiled Go binary in the GABS input directory.
Run `scripts/native_go_routine_acceptance.py --root /worker/run
--go-binary /inputs/gabs/rimgovernor-go --mood-review food`, then run the `mental`
case in a separate output directory. These cases require `MoodFixture` alongside
the routine acceptance fixtures. Keep workers sequential with two CPUs and 4 GB
of memory.

Require both the worker and `run/native-go-routine-acceptance/result.json` to pass.
The assertions cover native per-pawn inputs, policy-reference agreement, bounded
proposals, Manual and disabled restart. The mental case additionally requires a
paused, unchanged tick and no clock attempts. This scope establishes planning and
clock safety; actual relief execution and need recovery use the scenarios below.

Set `RIMGOVERNOR_NATIVE_MOOD_CAPTURE` to the exported
`run/native-go-routine-acceptance` directory and run
`go -C go test -p 1 ./internal/observation -run '^TestNativeRoutineMoodReplay$' -count=1`
for captured native projection, policy and durable-history replay.

## Relief execution acceptance

Prepare private [Linux game inputs](docker-inputs.md). Build the companion with
`-p:MoodFixture=true` and the documented Linux reference paths, and copy its output
into a fresh private mod snapshot. The fixture adds `test/mood_setup`, which is
excluded from production builds and the model gameplay gateway. It seeds a pawn's
need deficit and a known timetable/job; subsequent recovery uses normal game ticks.
Never install this fixture into a player game or replace a running worker's DLLs.

Set `RIMGOVERNOR_LINUX_GAME`, `RIMGOVERNOR_WORKER_MODS`, `RIMGOVERNOR_WORKER_PROFILE` and
`RIMGOVERNOR_LINUX_GABS` to those input directories. Set `RIMGOVERNOR_WORKER_OUTPUT` to a
new empty directory under the task's `.rimgovernor/`. Build the current worktree's
worker image, then run this PowerShell command with a unique container name/tag:

```powershell
docker build -f containers/Dockerfile --target worker -t rimgovernor-worker:mood .
docker run --rm --init --memory 4g --cpus 2 --name rimgovernor-mood-test `
  --mount "type=bind,source=$env:RIMGOVERNOR_LINUX_GAME,target=/inputs/game,readonly" `
  --mount "type=bind,source=$env:RIMGOVERNOR_WORKER_MODS,target=/inputs/mods,readonly" `
  --mount "type=bind,source=$env:RIMGOVERNOR_WORKER_PROFILE,target=/inputs/profile,readonly" `
  --mount "type=bind,source=$env:RIMGOVERNOR_LINUX_GABS,target=/inputs/gabs,readonly" `
  --mount "type=bind,source=$env:RIMGOVERNOR_WORKER_OUTPUT,target=/worker" `
  rimgovernor-worker:mood --unity-gc-time-slice 0 -- python /app/scripts/mood_acceptance.py
```

Require exit code zero and `run/mood-result.json` with `passed: true`. The report
retains installed schema, colony/load identity, setup, compiled-action receipts,
native tick windows, full pawn readbacks and per-case results. Input hashes and
game/controller logs remain beside it. Failed runs retain partial cases; use fresh
output directories after changes.

Append `--scenarios joy forced schedule mental stale_job stale_schedule` to select
cases. A passing selected run certifies only the listed cases. Keep earlier failed
reports when repeating or splitting the matrix; resource limits bound this worker
without stopping unrelated containers.

Positive cases require actual rest, food and recreation recovery to at least 0.5,
unchanged timetable hours and ordinary non-player-forced jobs. Negative cases
require native refusal of player-forced work, Work-time recreation, an active
mental break and stale job/schedule identities without replacing the pawn's job.
The probe uses shared goal method
compilation, plan commitment, Hands and native outcome reconciliation. It performs
no model inference. These bounded cases do not establish arbitrary long-term mood
stability or a guaranteed remedy for every thought, relationship or ideology need.

Run `controller_tests/test_mood_control.py` for unknown observations, thresholds,
hysteresis, alternative methods, active-break suspension and uncertain-write
reconciliation across load/player-direction boundaries. Fixture tests alone do not
establish native need recovery.
