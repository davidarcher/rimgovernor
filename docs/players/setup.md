# Windows setup

[Player guide](README.md)

The current launcher uses a prepared colony fixture. A clean checkout needs:

- Python 3.12+, Node.js 22+, pnpm and the .NET SDK.
- RimWorld 1.6, Harmony and RimBridgeServer.
- GABS v1.1.1 at `.rimgovernor/bridge/gabs/gabs-v1.1.1-windows-amd64/gabs.exe`.
- The required `RimGovernor-tribal8-baseline.rws` in an existing RimWorld profile.
- LM Studio for chat, with its local server at `http://127.0.0.1:1234/v1`.
  Autopilot does not require a model.

Game files, GABS and the baseline save are not in Git; setup does not download them.

## Build and prepare

With RimWorld closed, run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1
powershell -ExecutionPolicy Bypass -File scripts\build_observation_bridge.ps1 -Install
.venv\Scripts\python.exe scripts/prepare_bridge_trial.py --source-profile "C:/path/to/RimWorld-profile" --rimworld "C:/path/to/RimWorld" --observations
```

Replace the two example paths with your profile and game installation. Preparation
checks the required save's hash and creates a private profile, bridge configuration
and fixture manifest under `.rimgovernor/bridge`. It preserves the source profile.
Use `--root` for a different output location. A different save cannot substitute for
the required fixture.

Continue with [launch](launch.md). Developers can use [Docker checks](../developers/testing/docker-checks.md)
without game files, or [Linux input preparation](../developers/testing/docker-inputs.md)
for native scenarios.
