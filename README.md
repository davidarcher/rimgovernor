# RimBot

A local RimWorld colony controller using **RimBridgeServer**, the **RimBot Colony
Bridge** companion, GABS, Python and a React dashboard. RIMAPI is no longer a
runtime dependency or supported backend.

## Launch

Autopilot runs without a model. For interactive chat, start LM Studio's local
server with the configured model loaded (Qwen3.5-9B by default), then run:

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
- **Autopilot:** live food/wood/shelter readings, verified gates, goals/blockers and editable deterministic targets.
- **Projects:** long-term/current plan and native order receipts.
- **Activity:** outcomes with tool details collapsed.

Snapshots refresh every few seconds; this is not a continuous video stream.
The deterministic controller owns routine operation. Player chat uses a local LLM
as command interpreter and advisor. Both paths share persistent goals, resource
policies, validation and Hands. Explicit chat actions can dispatch in Manual while
time stays paused; Automate also runs routine work. Receipts do not mean pawn
labor has finished. See [the architecture](docs/ARCHITECTURE.md).

## Optional local model roles

Interactive chat uses the `strategist` model role; autopilot needs no inference. To configure a generic 4B analyst, copy/edit
`config/models.example.json`, then restart the controller with:

```powershell
.\launch.cmd -ModelsConfig config\models.example.json
```

Role names are `strategist`, `analyst`, `architect`, and `critic`. All optional roles
can be omitted or point at the same loaded model. Consultations happen only when
the strategist asks a specific question; there is no domain-manager fan-out.

## Project documentation

- [Architecture](docs/ARCHITECTURE.md): runtime pieces, ownership, contracts and data flow.
- [Backlog](docs/BACKLOG.md): prioritized implementation, audit and gameplay acceptance work.
- [Testing](docs/TESTING.md): native checks, real-model probes, headless campaigns and benchmarks.

Plans, projects and notes persist by saved colony identity/map. Save after identity
attachment to retain it across game restarts. Strategy cards are available through
planner retrieval; optional advisers remain read-only. The combined autonomous
eight-tribal starter foothold is not yet demonstrated.

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

For real-model and native gameplay verification, use the [testing runbook](docs/TESTING.md).
