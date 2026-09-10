# Run native Docker acceptance

[Documentation](../README.md)

Run the two-game assertions against [prepared Linux inputs](docker-inputs.md). This
runner observes both the controller and RimWorld.

Run commands from the task worktree root. Use private, stable input snapshots and a
fresh output directory; preserve failed results.

## Run the two-game check

Run two separate Compose projects with automatic free loopback ports and native
clock/checkpoint verification:

```powershell
python scripts/container_native_acceptance.py --game <linux-game> --mods <private-mods> --profile <prepared-profile> --gabs <linux-gabs-directory> --image rimbot-worker:my-task --output .rimbot/docker-native-01
```

The runner builds/pins the worker image, loads two private copies of the baseline,
advances one colony while the other remains paused, stops the first project and requires
a fresh native read from the survivor. It then saves a paired native and controller
checkpoint. Both projects are removed afterward; output trees, logs, input hashes,
checkpoint and result manifest remain. Failures are retained. Add `--image <tag>
--no-build` to use an existing image, or `--startup-timeout 480` for slow Windows bind
mounts. `run/staging.json` measures the input-copy time.

Replace the angle-bracket placeholders with existing absolute paths, quoting paths with
spaces. `--gabs` is a directory containing `gabs`, not the executable path. Unlike
manual Compose setup, do not pre-create `--output`; the runner creates it and supplies
the input/output/port environment variables itself. Startup timeout defaults to 240
seconds. The runner does not send player chat or measure inference; LM Studio is needed
when subsequently testing model-dependent behavior.

## Inspect outcomes and cleanup

Require exit code 0 and `result.json` with `passed: true`; inspect its cleanup and
checkpoint hash results too. Each numbered worker directory retains `compose.log`,
`container.log`, `cleanup.log` and `run/` evidence. For startup failures inspect
`run/Player.log`, `run/staging.json` and, in rendered mode, `run/display/`. Missing
input paths, a Windows game/GABS binary, mismatched mods or a reused output require
fixing the inputs and choosing a fresh run directory. Do not silently retry native
crashes or clean up other tasks with global Docker prune commands.

## Scope of the result

The current runner's assertions cover lifecycle, not completed pawn work. Docker can run
controller and native outcome assertions together; use or port gameplay scenarios to
verify actual pawn work. Reusable scenario and recorder improvements are tracked under
B17 in [BACKLOG.md](../BACKLOG.md). Throughput needs separate measurement. Startup
failures are never retried silently. Remaining probes with hard-coded Windows paths must
be ported before use in containers.

## Add rendering and player-input checks

Headless remains the default. For rendered tests set `RIMBOT_DISPLAY=xvfb`,
`RIMBOT_DISPLAY_RESOLUTION=1280x720` (640x480 through 3840x2160) and
`RIMBOT_DISPLAY_RENDERER=llvmpipe`. Each container owns Xvfb `:99` in its own namespace
with TCP disabled; no host display socket or desktop focus is used. The worker verifies
software OpenGL before launching the controller, sets private resolution/fullscreen
preferences with UI scale 1, removes the HeadlessRim active package in its private
profile and uses Unity OpenGL rendering. The rendered profile does not require the
HeadlessRim DLL. Game logs remain in `run/Player.log`; `run/display` retains Xvfb,
display capability and renderer logs, plus exit status. Startup/display failures fail
the worker without retries. The display stays alive through controller cleanup and stops
with its container.

Add `--display xvfb --resolution 1280x720` to the native acceptance command to retain
exact-resolution `frame.png` for each worker and a changed `survivor.png` after its peer
stops and native camera pan completes, alongside normal clock/checkpoint evidence. The
survivor must remain at its paused native tick. Inspect these frames for actual colony
content; PNG presence and size alone do not establish visual correctness. Use a fresh
output for a matching headless comparison. Frame transport, input gestures and sustained
rendering overhead require their own acceptance under B17/B18.

For B18 handoff and stable-ID selection, also pass `--player-input` with `--display
xvfb`. The survivor acquires a lease, rejects other viewers and stale credentials,
selects a current-map colonist, confirms the selection through a separate native read,
clears it and releases into Manual. The probe renews its lease like the browser and
retains per-request evidence in `player-input.json`. These checks do not exercise raw
image coordinates, drag/modifiers or WebRTC.

## Verify continuous video and native input schemas

For continuous video, the worker image includes the `video` extra. Run
`scripts/video_stream_acceptance.py --source-root /worker/run --output
/worker/video-acceptance --seconds 20 --input-probe` as the command of a fresh
rendered `rimbot.container_worker`. Stage this task's companion DLLs before launch;
the output must be new. The probe resolves the configured Linux or Windows GABS,
retains installed input schemas, receives native frames through aiortc, saves a
decoded PNG and checks unchanged paused ticks and peer cleanup. Inspect the PNG.
`--input-probe` checks map click and shift-drag selection against separate native
readbacks and records right-click results; a click without a menu is not menu
acceptance. These are direct native contracts, not browser gesture acceptance.
The report's capture-to-encoder ages include framebuffer readback. Delivered fps
and these ages do not measure desktop Chrome display latency, Docker ICE routing,
input-to-display latency or simulation cost. Do not bind-mount an unbuilt controller
over a built test image: it hides the dashboard assets needed by HTTP tests.

## Verify camera and ownership boundaries

Add `--camera-acceptance` with `--display xvfb` to exercise the current player
camera API in the first worker while its peer stays paused. The probe reads fresh
native geometry through `/api/camera/state?session_id=...`, checks four pan
directions, reaches both discovered zoom bounds and verifies clamping on another
request. Video heartbeats keep rendering active during navigation; owner heartbeats
continue during capture waits and stop for the explicit expiry test. The probe
retains initial, minimum/maximum zoom and takeover PNGs. It verifies
exclusive ownership, wrong/stale/released credentials, non-owner time controls,
actual lease expiry and takeover, and an unchanged native paused tick after the
pan sequence, each zoom boundary and takeover. Captures must advance the
controller camera version; a changed image hash alone does not establish freshness.
Rejected requests must leave native camera geometry unchanged.
Every native write is sent once; failures retain partial `camera_controls` evidence
and stop the probe. A GABS ownership-claim failure on a read is an infrastructure
failure; partial checks do not establish complete camera acceptance. The probe
requires a baseline whose initial camera is far enough
from map edges for ten-cell moves. It does not certify edge clamping, browser
lifecycle events, pending pawn work, selection or pointer/drag gestures.

## Verify environmental observation and cooking fallback

Build the task-private companion with `-p:DisasterFixture=true` in addition to the
Linux reference properties from [input preparation](docker-inputs.md), and stage
its observation and identity DLLs into a fresh private mod snapshot. The fixture
tool is excluded from normal builds and from model execution. Do not install it
into a shared game.

Run `python scripts/disaster_recovery_acceptance.py` as the command of a fresh
`rimbot.container_worker`, with that mod snapshot and the prepared baseline. Use
`--unity-gc-time-slice 0` for the accepted container startup setting and an explicit
`--gabs` executable path if the input release is nested. The probe reads
`RIMBOT_BRIDGE_ROOT`; it does not attach to a host game or require inference.

Require process exit 0 and `run/disaster-result.json` with `passed: true` and
`stopped: true`. Retain the result, `inputs.json`, `staging.json`, native logs and
the SQLite store. The cases cover temporary and permanent condition reads,
native expiry, selection of the cooking fallback, completed campfire construction
at the planned cell, and accessible wood stock decrease. Fixture-created
infrastructure is not construction acceptance. These cases do not establish
prolonged environmental survival, toxic exposure control, infrastructure repair
or full disaster recovery; those remain in B28.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
