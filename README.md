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
The planner queries native tool schemas and observations, and can issue native
orders in Automate. Receipts do not mean pawn work has finished.

## Remaining migration work

The retired RIMAPI implementation is preserved in Git at `9209b74`. Its richer
project reconciliation, visual architect/reservations, colony detail inspectors,
and persistent session restoration have **not** been ported to the bridge.
They are not available through a hidden fallback. Existing strategy references
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
