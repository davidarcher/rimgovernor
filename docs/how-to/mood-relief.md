# Verify mood relief in Docker

[Documentation](../README.md) · [Mood contracts](../reference/mood-control.md)

Prepare private [Linux game inputs](docker-inputs.md). Build the companion with
`-p:MoodFixture=true` and the documented Linux reference paths, and copy its output
into a fresh private mod snapshot. The fixture adds `test/mood_setup`, which is
excluded from production builds and the model gameplay gateway. It seeds a pawn's
need deficit and a known timetable/job; subsequent recovery uses normal game ticks.
Never install this fixture into a player game or replace a running worker's DLLs.

Set `RIMBOT_LINUX_GAME`, `RIMBOT_WORKER_MODS`, `RIMBOT_WORKER_PROFILE` and
`RIMBOT_LINUX_GABS` to those input directories. Set `RIMBOT_WORKER_OUTPUT` to a
new empty directory under the task's `.rimbot/`. Build the current worktree's
worker image, then run this PowerShell command with a unique container name/tag:

```powershell
docker build -f containers/Dockerfile --target worker -t rimbot-worker:mood .
docker run --rm --init --name rimbot-mood-test `
  --mount "type=bind,source=$env:RIMBOT_LINUX_GAME,target=/inputs/game,readonly" `
  --mount "type=bind,source=$env:RIMBOT_WORKER_MODS,target=/inputs/mods,readonly" `
  --mount "type=bind,source=$env:RIMBOT_WORKER_PROFILE,target=/inputs/profile,readonly" `
  --mount "type=bind,source=$env:RIMBOT_LINUX_GABS,target=/inputs/gabs,readonly" `
  --mount "type=bind,source=$env:RIMBOT_WORKER_OUTPUT,target=/worker" `
  rimbot-worker:mood --unity-gc-time-slice 0 -- python /app/scripts/mood_acceptance.py
```

Require exit code zero and `run/mood-result.json` with `passed: true`. The report
retains installed schema, colony/load identity, setup, compiled-action receipts,
native tick windows, full pawn readbacks and per-case results. Input hashes and
game/controller logs remain beside it. Failed runs retain partial cases; use fresh
output directories after changes.

Positive cases require actual rest, food and recreation recovery to at least 0.5,
unchanged timetable hours and ordinary non-player-forced jobs. Negative cases
require native refusal of player-forced work, Work-time recreation and an active
mental break without replacing the pawn's job. The probe uses shared goal method
compilation, plan commitment, Hands and native outcome reconciliation. It performs
no model inference. These bounded cases do not establish arbitrary long-term mood
stability or a guaranteed remedy for every thought, relationship or ideology need.

Run `controller_tests/test_mood_control.py` for unknown observations, thresholds,
hysteresis, alternative methods, active-break suspension and uncertain-write
reconciliation across load/player-direction boundaries. Fixture tests alone do not
establish native need recovery.
