# Set up a Windows development checkout

[Documentation](../README.md)

Requires Python 3.12+, Node.js 22+, pnpm, RimWorld 1.6, Harmony, RimBridgeServer, and
the compiled companion. GABS is the bridge process, not an HTTP game proxy.

```powershell
powershell -ExecutionPolicy Bypass -File .\setup.ps1

# With RimWorld closed and the .NET SDK available:
powershell -ExecutionPolicy Bypass -File scripts\build_observation_bridge.ps1 -Install
```

Prepare the isolated profile using `scripts/prepare_bridge_trial.py` with
`--observations`, `--source-profile` pointing to the normal RimWorld save folder, and
`--rimworld` pointing to the game install. This requires the checkpointed
`RimBot-tribal8-baseline.rws` fixture. It copies the fixture and strips its retired mod
component from the copy, never the original.

The existing local installation keeps GABS v1.1.1 at
`.rimbot/bridge/gabs/gabs-v1.1.1-windows-amd64/gabs.exe` and its configuration at
`.rimbot/bridge/config/config.json`. These binaries and the test save are not in Git. A
clean checkout needs those prerequisites; setup.ps1 does not download or create them. LM
Studio defaults to http://127.0.0.1:1234/v1.

## Prepare the Windows fixture

With the required baseline present in the source profile, run:

```powershell
.venv\Scripts\python.exe scripts/prepare_bridge_trial.py --source-profile "C:/path/to/RimWorld-profile" --rimworld "C:/path/to/RimWorld" --observations
```

Replace both paths with your existing profile and game installation. The script checks
the baseline hash and refuses a different save. Its default output root is
`.rimbot/bridge`; `--root` selects another location. It writes a private profile, GABS
configuration and fixture manifest. This preparation does not supply the GABS
executable. Stop on a missing prerequisite rather than substituting an unrelated save
and treating it as the baseline.

## Related reading

[Launch a prepared colony](launch.md) · [Run local checks](local-checks.md) · [Linux
Docker inputs](docker-inputs.md)
