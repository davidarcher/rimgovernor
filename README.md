# RimBot

An external RimWorld colony controller with a local web dashboard. Python runs the
manager; RIMAPI runs inside the game. The dashboard is customized from RIMAPI
Dashboard, retaining its colony inspectors alongside our management workspace.

## Launch

From this repository, run:

```powershell
.\launch.cmd
```

This starts the Python controller in the background, launches RimWorld with
`-quicktest`, and opens http://127.0.0.1:8787. Existing controller/game processes
are reused. The game is not restarted or replaced. Choose **Automate** in the
web dashboard when ready; **Manual** leaves control with you.

Equivalent PowerShell command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\launch.ps1
```

Options:

```powershell
.\launch.cmd -NoGame                 # Dashboard/controller only
.\launch.cmd -NoBrowser              # Do not open another browser tab
.\launch.cmd -Port 8788              # Alternate dashboard/controller port
.\launch.cmd -NormalGame             # Open the main menu instead of quicktest
```

RimWorld must have **Harmony and RIMAPI enabled**, and the old **RimBot mod disabled**.
Enable **Run in background** in RimWorld so it advances while viewing the browser.
The launcher does not change mod selection, install a DLL, or start LM Studio.
Start LM Studio's local server with your selected Qwen model. Default addresses
are RIMAPI `http://127.0.0.1:8765` and LM Studio `http://127.0.0.1:1234/v1`.
Both are configurable in the dashboard.

## First-time setup

Python 3.12+, Node.js 22+ and pnpm are required to build locally. Existing Codex
bundled runtimes are detected when available.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\setup.ps1
.\launch.cmd
```

The current RIMAPI contract is pinned in `integrations/RIMAPI`. To retrieve source:

```powershell
git submodule update --init integrations/RIMAPI
```

The installed RIMAPI mod remains a separate dependency. The controller discovers
which pinned endpoints the running server actually provides and reports gaps.

## Develop and test

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
# Controller hot reload (returns to Manual when code reloads):
.\.venv\Scripts\python.exe -m rimbot --reload
# Frontend development in a second terminal:
cd dashboard
pnpm dev
```

The production build serves both the UI and controller from one loopback port.
The Vite development server proxies the controller on port 8787. No paid-provider
fallback exists. Model reasoning uses Qwen's on/off setting; output allowance and
context size are configurable. No tiny action quota or tool rotation is used.

## State and logs

- `.rimbot/colony.sqlite`: per-colony objectives, plans, tracked work and activity.
- `.rimbot/logs/`: timestamped controller startup/output/error logs.
- **Export log** in the dashboard: structured activity history.
- **Live feed**: locally relayed game-camera JPEG frames; snapshots are also available.

## Migration and provenance

See `docs/EXTERNAL_CONTROLLER.md` for the architecture, validation boundary and
remaining integrations; `THIRD_PARTY.md` records source revisions and licenses.
The retired RimBot C# mod, packaging, and test harness have been removed. Its
history remains in Git at `bf9a1eb`. Native game integration lives in
`integrations/RIMAPI`; the Python controller and dashboard are the supported runtime.

Tests verify contracts, orchestration, error handling, HTTP transports and UI
behavior. They do not establish real-model gameplay competence. Real RIMAPI video
and colony actions require a loaded-game playtest.
