# Windows setup

[Player guide](README.md)

A clean checkout needs:

- Go 1.27.1 (`.go-version`), Node.js 22+, pnpm and the .NET SDK (for the native
  mod and the shared Protobuf contracts), all on `PATH`.
- RimWorld 1.6 with Harmony and RimBridgeServer from Steam (the launcher finds
  them through Steam's libraries).
- The Microsoft Edge WebView2 Runtime (preinstalled on Windows 11).

There is no local-model requirement: the autopilot (routine work) needs no
model. Chat is optional and needs LM Studio serving the model you name in the
launcher's settings.

## Build the launcher once

From `go/`:

```powershell
$env:GOTOOLCHAIN = 'go1.27.1'; $env:CGO_ENABLED = '0'
go build -ldflags -H=windowsgui -o ..\RimGovernor.exe ./cmd/launcher
```

Then double-click `RimGovernor.exe` in the repository root. It must stay inside
the checkout. Rebuild it only when `go/cmd/launcher` changes.

## What the launcher prepares

On open, the launcher checks each artifact and rebuilds whatever is stale,
showing its output in the Log panel:

- **GABS bridge**: downloads the pinned GABS v1.1.1 release into
  `.rimgovernor/bridge/gabs/` and verifies its SHA-256.
- **Game layout**: a private RimWorld copy at `.rimgovernor/native-rimworld`
  (junctioned to your Steam install) and the GABS config, profile and baseline
  save under `.rimgovernor/bridge`. Your normal saves and mod list are untouched.
- **Native mod**: the production build of `integrations/rimgovernor-native`,
  rebuilt when its sources change. It is never replaced while the private game
  copy runs; close the game and press **Check again**.
- **Go controller**: `.rimgovernor/go/rimgovernor.exe`, rebuilt every open.
- **Dashboard**: `dashboard/dist`, rebuilt with pnpm when its sources are newer.

Continue with [launch](launch.md).
