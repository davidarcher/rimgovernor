# Windows setup

[Player guide](README.md)

A clean checkout needs:

- The .NET SDK 10 on `PATH` (builds the launcher, the native mod and the brain).
- RimWorld 1.7 (Waylith) with Harmony from Steam (the launcher finds them through Steam's
  libraries). Do not also subscribe to NativeServer: the RimGovernor mod
  is not designed to run beside it.
- The Microsoft Edge WebView2 Runtime (preinstalled on Windows 11).

There is no local-model requirement: the autopilot needs no model.

## Build the launcher once

Double-click `RimGovernor.cmd` in the repository root. It builds the launcher
(`RimGovernorLauncher.exe`, seconds when nothing changed) and opens it; the .NET SDK 10 must be
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
There is no separate brain: the brain runs inside the game.

Continue with [launch](launch.md).
