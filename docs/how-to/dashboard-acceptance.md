# Verify the dashboard and video

[Documentation](../README.md)

Separate native video acceptance from synthetic browser lifecycle checks and
player-facing behavior.

Run commands from the repository root. Native probes require a disposable prepared
profile and their stated fixture; run `--help` for the selected script. Keep outputs
under a fresh `.rimbot/` directory, preserve failures, and never replace installed DLLs
while any RimWorld instance is running. Container inputs use private snapshots.

`setup.ps1` installs the `video` extra. For an existing Python environment run `python
-m pip install -e '.[video]'`. Build/install the observation companion only with every
RimWorld process stopped; older companions use snapshot fallback. Run
`scripts/video_stream_acceptance.py --source-root <prepared-root> --output
<fresh-directory>` with `controller` on `PYTHONPATH` to receive native frames over a
real local WebRTC connection in a disposable paused colony. It checks decoded
dimensions, advancing frames, peer cleanup and unchanged paused native tick. Restore any
temporarily installed companion after the owned game stops. This probe measures
native-to-aiortc delivery, not Chrome capture-to-display latency, hardware encoding,
input safety or simulation throughput.

In Chrome, verify Live video, Pause video retaining the current frame, resume,
hidden-tab cleanup, load changes, multiple viewers and snapshot fallback after a stream
stall. Resize/fullscreen must retain the image aspect ratio. The 30–60 fps, latency and
CPU/GPU/TPS acceptance work remains in B18.

For browser lifecycle work without a game or installed-DLL changes, build the dashboard
and run `scripts/video_browser_fixture.py --port 8791` with `controller` on
`PYTHONPATH`, then open `http://127.0.0.1:8791/fixture`. Its explicit synthetic controls
stall/resume frames, change session identity and alternate landscape/ portrait
resolution. Verify automatic reconnect, retained images, Pause video, Expand/Exit
fullscreen and session cleanup. The fixture never contacts GABS or starts a game. Use
`/api/video/status` to inspect bounded delivery counters; hover the video badge for
browser decode/jitter statistics. This is browser and protocol acceptance only; retain
native and Chrome performance work in B18.

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

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
