# Headless test campaigns

Use the existing eight-tribal baseline, local LM Studio server and installed
bridge/headless mods. From the repository:

```powershell
.venv\Scripts\python.exe scripts/headless_iterations.py --iterations 20 --parallel 2 --output .rimbot/campaign-new
```

Choose a fresh output directory. Start with two workers; eight is the configured
maximum, not a measured optimum. Each process gets its own Python controller,
SQLite state, save/config profile, log and GABS runtime directory. GABS allocates
the endpoint; workers use direct launches with PID ownership and no broad
process-name cleanup fallback. Installed game/mod files are shared read-only.
Do not replace DLLs during a campaign.

The runner starts small batches and stops dispatching after a usable result;
already-running siblings finish and keep their evidence. Each worker imports the
current code independently. Commit changes between batches for clean revision
attribution. The initial observation window is 120 seconds; issuing an action
extends it to allow another 120 seconds for execution. This is a short startup
probe, not a multi-day survival test. Simulation uses ordinary Superfast speed.

The narrow success check requires eight nearby completed beds/spots, a nearby
stockpile of at least nine cells, allowed starting pemmican, and eight living
colonists. It does not certify roofing, nutrition sustainability or good strategy.
`summary.json` contains every completed trial; each trial retains its detailed
`result.json` and SQLite events. Generated evidence stays outside Git.

## Verified on 2026-09-08

`scripts/parallel_headless_smoke.py --output .rimbot/parallel-new` loaded two
independent native games. One advanced from tick 38 to 408 while the other stayed
at 38. Killing the first through its own GABS session left the second responsive.
The test took 13.3 seconds including startup. This verifies lifecycle/clock
isolation, **not** concurrent model throughput or cloud performance. The expected
socket-close message from killing the first game is not a failed assertion.

Multiple requests share the same LM Studio model/GPU. Benchmark completed useful
tests per minute and peak memory before increasing worker count. Parallel
simulation does not imply proportional inference speedup.

## Linux/container portability — still to implement

[HeadlessRim](https://github.com/IlyaChichkov/HeadlessRim) supplies a Linux Docker
example with dummy audio, Xvfb and `-batchmode -nographics`; its sample starts
RIMAPI. We use the native RimBridge/GABS path instead. Remaining work:

1. Parameterize the currently Windows-specific GABS executable and game paths.
2. Supply licensed Linux game files and compatible installed mods as mounted
   inputs, with one writable save/profile volume per worker.
3. Package the Python controller and Linux GABS launcher; configure an explicit
   inference-service address rather than the current loopback-only setting.
4. Verify bridge discovery, startup, independent clocks, shutdown and lost-worker
   cleanup in containers. Preserve logs/results outside ephemeral containers.
5. Compare native Windows and Linux cost/throughput using identical fixtures and
   model settings. Account for simulation, inference, startup and storage costs.

No Docker/cloud deployment or Linux gameplay acceptance has been performed yet.
