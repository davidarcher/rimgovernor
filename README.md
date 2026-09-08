# RimBot

A local RimWorld colony controller using **RimBridgeServer**, the **RimBot Colony
Bridge** companion, GABS, Python and a React dashboard. RIMAPI is no longer a
runtime dependency or supported backend.

## Launch

Start LM Studio's local server with Qwen3.5-9B loaded, then run:

```powershell
.\launch.cmd
```

The dashboard opens at http://127.0.0.1:8787 in Manual mode. If RimWorld is not
running, the launcher starts the prepared isolated eight-tribal fixture. If a
native bridge game/controller is already running, it is reused. Enable Run in
background in RimWorld. Automate pauses for its initial review, then resumes.

```powershell
.\launch.cmd -FreshGame              # Require a new fixture; close existing game/controller first
.\launch.cmd -NoGame                 # Connect to an already running bridge game
.\launch.cmd -NoBrowser
.\launch.cmd -Model qwen3.5-4b        # Use this model if loaded in LM Studio
```

`launch-bridge.ps1` forwards to the same launcher. There is no backend selector.
The old NormalGame/QuickTest flags are retired; this launcher uses the prepared
GABS profile. It does not silently alter your normal saves or mod selection.

## Setup

Requires Python 3.12+, Node.js 22+, pnpm, RimWorld 1.6, Harmony, RimBridgeServer,
and the compiled companion. GABS is the bridge process, not an HTTP game proxy.

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
# With RimWorld closed and the .NET SDK available:
powershell -ExecutionPolicy Bypass -File scripts\build_observation_bridge.ps1 -Install
```

Prepare the isolated profile using `scripts/prepare_bridge_trial.py` with
`--observations`, `--source-profile` pointing to the normal RimWorld save folder,
and `--rimworld` pointing to the game install. This requires the checkpointed
`RimBot-tribal8-baseline.rws` fixture. It copies the fixture and strips its retired
mod component from the copy, never the original.

The existing local installation keeps GABS v1.1.1 at
`.rimbot/bridge/gabs/gabs-v1.1.1-windows-amd64/gabs.exe` and its configuration at
`.rimbot/bridge/config/config.json`. These binaries and the test save are not in
Git. A clean checkout needs those prerequisites; setup.ps1 does not download or
create them. LM Studio defaults to http://127.0.0.1:1234/v1.

## Current interface

- **Colony:** resizable game snapshots, player chat and a short next-step summary.
- **Projects:** long-term/current plan and native order receipts.
- **Activity:** outcomes with tool details collapsed.

Snapshots refresh every few seconds; this is not a continuous video stream.
One strategist reads compact state and commits a durable structured plan. Deterministic
Hands executes validated semantic steps in Automate; optional advisers cannot write
orders or commit goals. Unchanged observations do not cause timed model reviews.
Receipts do not mean pawn work has finished. See [the active architecture](docs/STRATEGIC_BRAIN.md).

## Optional local model roles

The default uses only the strategist. To configure a generic 4B analyst, copy/edit
`config/models.example.json`, then restart the controller with:

```powershell
.\launch.cmd -ModelsConfig config\models.example.json
```

Role names are `strategist`, `analyst`, `architect`, and `critic`. All optional roles
can be omitted or point at the same loaded model. Consultations happen only when
the strategist asks a specific question; there is no domain-manager fan-out.

## Test-only speed benchmark

With the controller and disposable game closed:

```powershell
.venv\Scripts\python.exe scripts\native_speed_benchmark.py --seconds 5 --repeats 2
```

This uses native `play_for` with boosted Ultrafast/forced-speed support, restoring
Paused and disabling the boost afterward. It omits dashboard captures but does not
disable Unity rendering. Interrupted samples are explicitly marked. Measurements
are local in `.rimbot/bridge/speed-benchmark.json`. This option is not exposed to the
strategist during normal play.

## Remaining migration work

Headless native testing is available with `launch.ps1 -Headless` after installing
the source-built test patch. See [headless setup and measured limits](docs/TEST_SPEED.md).

The retired RIMAPI implementation is preserved in Git at `9209b74`. Its richer
project reconciliation, visual architect/reservations, colony detail inspectors,
and full session lifecycle handling have **not** all been ported to the bridge.
The bridge now restores chat/plans/projects using a saved colony identity and tracks building/zone targets. Save the colony after its identity is attached to retain that identity across game restarts. Richer scheduling and architect features are not available through a hidden fallback. Existing strategy references
remain under `controller/rimbot/data/strategies` for reuse; they are not currently
injected into the native planner.

See [the migration backlog](docs/RIMBRIDGE_MIGRATION.md). Historical research and
implementation notes are under `docs/archive/rimapi`; their paths and claims
refer to the retired implementation.

## Development

```powershell
.venv\Scripts\python.exe -m pytest -q
.venv\Scripts\python.exe scripts\generate_bridge_observation.py --check
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Normal `python -m rimbot` starts only the bridge backend. Controller tests verify
protocols and decisions, not sustained colony survival. Native source provenance
is in `integrations/colony-bridge/PROVENANCE.md`; upstream license notices retained
under `third_party` also cover inherited dashboard material. No model API calls
or game mutations run as part of the unit test suite.

For a bounded **real model** test, launch a fresh colony with an empty plan and
leave the controller in Manual, then run:

```powershell
.venv\Scripts\python.exe scripts\live_planner_probe.py --seconds 180
```

This enables automation and lets the model issue game orders. It records time to
first order, completed steps, rejected calls, and final state in
`.rimbot/live-planner-probe.json`, then returns the same controller/session to
Manual. It does not reload a save or reset an existing plan. Start each comparison
from the same fixture. An `orders_observed` result is progress, not proof of a
completed starter base or colony survival.
