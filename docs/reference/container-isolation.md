# Container and worker isolation

[Documentation](../README.md)

This reference describes process, binary and display isolation. For commands, use
[native Docker acceptance](../how-to/docker-native.md).

The dashboard polls compact state and keeps drafts/last good data through refreshes.
The game view uses WebRTC where supported and periodic snapshots as fallback; viewer leases drive native render demand. `integrations/headless-rim` removes presentation paths in isolated test
profiles. Each campaign worker owns a separate controller, SQLite database, game
profile, GABS runtime and logs. Windows workers share installed game/mod files
read-only. `container_worker.py` copies licensed Linux game/mod inputs into a fresh
container-local Linux directory before starting any game process; profiles, databases,
logs and checkpoints use the host output mount. Compose workers apply a recorded zero GC
time-slice setting to the private Unity boot configuration to mitigate early Mono
startup crashes; source inputs are unchanged. Each container owns its DLL snapshot and
records binary/profile hashes in `inputs.json`; no installed DLL swap is involved.
Inputs must remain stable during staging. Configuration may set `rimbot.gabsExecutable`
relative to the worker root (or absolute); legacy Windows profiles retain their existing
default. `scripts/container_checks.py` runs independent Linux controller suites against
one pinned image ID. `scripts/container_native_acceptance.py` creates two Compose
projects and verifies native clocks, peer survival, clean shutdown and a retained paired
checkpoint through the normal controller API. Optional `RIMBOT_DISPLAY=xvfb` workers use
a container-local Xvfb display and explicitly verified Mesa llvmpipe software OpenGL.
Rendered staging removes HeadlessRim from the private active mod list, sets its saved
display preferences and retains dimensions/renderer with input evidence. The GABS
transport explicitly inherits the display/software-renderer environment needed by its
owned game. The display supervisor keeps X alive during controller shutdown, fails on
display death and retains logs under `run/display`; it never restarts a failed game.
Headless remains the default.

Build output, saves, logs, binaries and measurements belong outside Git. Source
attribution stays beside integrations and in [THIRD_PARTY.md](../../THIRD_PARTY.md). Use
[TESTING.md](../TESTING.md) for verification and [BACKLOG.md](../BACKLOG.md) for all
unfinished work. New capabilities should extend native contracts, guarded execution and
observed postconditions, with focused tests and explicit gameplay acceptance. The
combined autonomous starter colony remains unproven.
