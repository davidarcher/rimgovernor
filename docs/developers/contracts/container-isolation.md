# Container and worker isolation

[Documentation](../../README.md)

This reference describes process, binary and display isolation. For commands, use
[native Docker acceptance](../testing/docker-native.md).

The dashboard polls compact state and keeps drafts/last good data through refreshes.
The game view uses frame-bound WebSocket video and periodic snapshots as fallback; viewer leases drive native render demand. `integrations/rimgovernor-native/src/Runtime/Headless` removes presentation paths in isolated test
profiles. Each campaign worker owns a separate controller, SQLite database, game
profile, GABS runtime and logs. Windows workers share installed game/mod files
read-only. `container_worker.py` copies licensed Linux game/mod inputs into a fresh
container-local Linux directory before starting any game process; profiles, databases,
logs and checkpoints use the host output mount. Compose workers apply a recorded zero GC
time-slice setting to the private Unity boot configuration to mitigate early Mono
startup crashes; source inputs are unchanged. Each container owns its DLL snapshot and
records binary/profile hashes in `inputs.json`; no installed DLL swap is involved.
Inputs must remain stable during staging. Configuration may set `rimgovernor.gabsExecutable`
relative to the worker root (or absolute); legacy Windows profiles retain their existing
default. `scripts/container_checks.py` runs independent Linux controller suites against
one pinned image ID. `scripts/container_native_acceptance.py` creates two Compose
projects and verifies native clocks, peer survival, clean shutdown and a retained paired
checkpoint through the normal controller API. Optional `RIMGOVERNOR_DISPLAY=xvfb` workers use
a container-local Xvfb display and explicitly verified Mesa llvmpipe software OpenGL
or accelerated D3D12 rendering.
Rendered staging removes HeadlessRim from the private active mod list, sets its saved
display preferences and retains dimensions/renderer with input evidence. The GABS
transport explicitly inherits the display/renderer environment needed by its
owned game. The display supervisor keeps X alive during controller shutdown, fails on
display death and retains logs under `run/display`; it never restarts a failed game.
Headless remains the default.

Prepared headless and rendered profiles pass `-rimgovernor-pause-on-load`. The colony
identity component pauses in its native loaded-game callback, before readiness
polling can advance the saved baseline. It does not change ticks or save content;
ordinary game launches without the flag retain their normal load behavior. Exact-tick
scenario assertions require the matching identity DLL in the private mod snapshot.

The named scenario runner adds resource limits, explicit repeated trials, JUnit results,
Docker resource samples and retained SQLite backups. `RIMGOVERNOR_FLIGHT_RECORDER` opts into
a bounded native request/response/error timeline with durable pre-dispatch requests and
runtime plan snapshots. Recording is disabled in ordinary runs. See the
[scenario procedure](../testing/native-scenarios.md) for coverage and retention limits.

Build output, saves, logs, binaries and measurements belong outside Git. Use
[TESTING.md](../testing/README.md) for verification and [BACKLOG.md](../../BACKLOG.md) for all
unfinished work. New capabilities should extend native contracts, guarded execution and
observed postconditions, with focused tests and explicit gameplay acceptance. The
combined autonomous starter colony remains unproven.
