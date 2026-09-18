# Windows setup

[Player guide](README.md)

The current launcher uses a prepared colony fixture. A clean checkout needs:

- Go 1.27.1 (`.go-version`), Node.js 22+, pnpm and the .NET SDK (for the shared
  Protobuf contracts).
- RimWorld 1.6, Harmony and RimBridgeServer.
- GABS v1.1.1 at `.rimgovernor/bridge/gabs/gabs-v1.1.1-windows-amd64/gabs.exe`.
- The baseline colony save, `scripts/fixtures/saves/RimGovernor-tribal8-baseline.rws`
  (committed; the launcher stages it into the profile it prepares).

Game files and GABS are not in Git; setup does not download them.
There is no local-model requirement: the autopilot (routine work) needs no
model. Chat is optional and needs LM Studio serving the model named by
`--chat-model`.

## Build and prepare

With RimWorld closed, run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build_native_mod.ps1 -RimWorldManagedDir "C:/path/to/RimWorld/RimWorldWin64_Data/Managed" -HarmonyAssembly "C:/path/to/0Harmony.dll" -RimBridgeSdkDir "C:/path/to/RimWorld/Mods/RimBridgeServer/1.6/Assemblies" -OutputRoot ".rimgovernor/native-build"
Copy-Item -LiteralPath ".rimgovernor/native-build/RimGovernor" -Destination "C:/path/to/RimWorld/Mods" -Recurse
```

Replace the example paths with your profile, game and dependency locations. Use a
fresh build output directory. Install only while all RimWorld instances are closed;
remove the old RimGovernorObservations/Headless packages from your disposable setup.

Prepare a private GABS profile under `.rimgovernor/bridge` by copying your baseline
save into it directly (create that save with the unified package enabled; old saves
are not migration inputs), then continue with [launch](launch.md).
