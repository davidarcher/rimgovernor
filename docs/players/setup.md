# Windows setup

[Player guide](README.md)

A clean checkout needs:

- Go (`go/.go-version`) and the .NET SDK (for the native
  mod and the shared Protobuf contracts), both on `PATH`.
- RimWorld 1.6 with Harmony from Steam (the launcher finds them through Steam's
  libraries). Do not also subscribe to RimBridgeServer: the RimGovernor mod
  carries its own GABP host and is marked incompatible with it.
- The Microsoft Edge WebView2 Runtime (preinstalled on Windows 11).

There is no local-model requirement: the autopilot needs no model.

## Build the launcher once

Double-click `RimGovernor.cmd` in the repository root. It builds the launcher
(`RimGovernorLauncher.exe`, seconds when nothing changed) and opens it; the pinned Go must be
installed.

## What the launcher prepares

On open, the launcher checks each artifact and rebuilds whatever is stale,
showing its output in the Log panel:

- **Game layout**: a private RimWorld copy at `.rimgovernor/native-rimworld`
  (junctioned to your Steam install) and the launch config, profile and baseline
  save under `.rimgovernor/bridge`. Your normal saves and mod list are untouched.
- **Native mod**: the production build of `integrations/rimgovernor-native`,
  rebuilt when its sources change. It is never replaced while the private game
  copy runs; close the game and press **Check again**.
- **Go controller**: `.rimgovernor/go/rimgovernor.exe`, rebuilt every open.

Continue with [launch](launch.md).
