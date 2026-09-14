# Verify the dashboard and video

[Documentation](../../README.md)

Separate native video acceptance from synthetic browser lifecycle checks and
player-facing behavior.

For direct native browser acceptance, start a private rendered Docker worker with
`python scripts/interactive_view_acceptance.py --output /worker/acceptance` as its
command. Publish the HTTP port on loopback and open it in the connected Chromium
browser. The harness serves the production dashboard and records allowlisted native
reads under `acceptance/native.jsonl`. Its `/test/load` endpoint reloads the disposable
baseline; `/test/held` reads the private X server's actual held-key/button state.
Run `python scripts/player_stream_acceptance.py --output /worker/acceptance/faults.json`
inside that worker, with browser ownership released, for disconnect, lease expiry,
competing viewers, stale camera/frame and load invalidation checks.

On Windows Docker with WSL GPU support, add `--gpus all`,
`-e NVIDIA_DRIVER_CAPABILITIES=compute,utility,video`,
`--mount type=bind,src=/usr/lib/wsl,dst=/usr/lib/wsl,readonly`,
`-e LD_LIBRARY_PATH=/usr/lib/wsl/lib -e MESA_D3D12_DEFAULT_ADAPTER_NAME=NVIDIA`,
and worker options `--display xvfb --renderer d3d12`. Startup must report accelerated
D3D12 OpenGL. Xvfb remains private with no host display sockets. GPU rendering and
NVENC encoding are separate capabilities; verify the native renderer and the stream's
`encoding` independently in `/api/video/status`.

Record browser capture-to-display median/p95, selection-to-display median/p95 and
sample counts from `/api/video/status`. Selection latency starts at pointer dispatch
and ends when the browser paints a frame with a changed native selection fingerprint.
It measures visible selection response, not completion of pawn work. Use native reads
to establish selection, zone cells, job execution, camera changes and held-input cleanup.
Compare `/test/cost` and native ticks over equal intervals with video on/off; capture
ceilings do not promise delivered FPS or simulation throughput.

[Native test prerequisites](README.md#native-test-prerequisites).

`setup.ps1` installs the `video` extra. For an existing Python environment run `python
-m pip install -e '.[video]'`. Build/install the observation companion only with every
RimWorld process stopped; older companions use snapshot fallback. Private Docker
staging copies task DLLs before launching its owned game.

In the connected Chromium browser, verify Live video, Pause video retaining the current frame, resume,
hidden-tab cleanup, load changes, multiple viewers and snapshot fallback after a stream
stall. Resize/fullscreen must retain the image aspect ratio and native input mapping.

For browser lifecycle work without a game or installed-DLL changes, build the dashboard
and run `scripts/video_browser_fixture.py --port 8791` with `controller` on
`PYTHONPATH`, then open `http://127.0.0.1:8791/fixture`. Its explicit synthetic controls
stall/resume frames, change session identity and alternate landscape/ portrait
resolution. Verify automatic reconnect, retained images, Pause video, Expand/Exit
fullscreen and session cleanup. The fixture never contacts GABS or starts a game. Use
`/api/video/status` to inspect bounded delivery counters. Synthetic lifecycle checks
do not establish native outcomes or performance.

Use a rendered prepared profile and the local web server for player-facing tests. Verify
that `/api/camera` supplies complete immutable PNG responses while native captures
advance; pause/play video must retain the last good frame. Check an actual local-model
request and verify its resulting goal or action in shared state.

On Autopilot, change a target or speed and confirm its effective persisted value. Reject
invalid threshold ordering and stale policy versions without altering the plan. Keep
unsaved drafts across background refreshes, including when chat changes settings.
Verification limits are read-only. The API requires the normal local mutation header and
current colony identity.

Stopping the controller may terminate its owned disposable game through the process
lifecycle. Preserve native saves before planned restarts; do not assume the game
survives killing the controller process. A player-facing session intentionally left
running needs its matching installed companion until that session is closed.

## Verify colonist dossiers

Run `scripts/pawn_images_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` with `controller` on `PYTHONPATH` and this task's companion DLLs
fixed for the entire game lifetime. On Windows, `--game-root <private-game>` selects
an isolated executable/mod directory. The probe never installs DLLs. It retains
portraits, native gear/thoughts, follow images, camera/selection readbacks and cleanup
in `result.json`. It points the main camera away from the pawn to test offscreen
culling, verifies an unchanged paused tick during captures and checks stale-session
refusal before a bounded interval of ordinary game time. Inspect the PNGs; protocol
success alone cannot establish correct rendering or weapon-icon appearance.
Add `--equip-preview` to verify ordinary pickup of a nearby unstacked ranged weapon,
confirm the exact equipped identity, and retain its portrait icon and follow image.
The disposable fixture must contain a capable pawn and an eligible ground weapon.

In the browser, open Colony, select a colonist and start their follow view. Check
actual worn gear, a native equipped-weapon icon, thoughts and job reports against
the game. Verify selection survives refresh failures and tab navigation, while
load/map changes clear it. Check no image requests while hidden, retained images
during failures, and sensible narrow-window layout. Test modded apparel/weapons,
multiple viewers, moving pawns, a removed pawn and simultaneous main-view video.
Measure rendering cost separately from the snapshot refresh interval.

## Related reading

[Testing](README.md) · [Issues](https://github.com/davidarcher/rimgovernor/issues)
