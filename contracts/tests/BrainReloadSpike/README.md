# Brain reload spike

Out-of-game proof for [brain hot reload feasibility](../../../docs/developers/architecture/brain-hot-reload-feasibility.md).
Loads `RimGovernor.BrainSpike.Brain.dll` from bytes into a collectible `AssemblyLoadContext`,
runs it, disposes it, unloads it and checks that the context is collected. Needs only the
net10 SDK; no game, no Harmony.

```
dotnet build Host/Host.csproj -c Release
dotnet Host/bin/Release/net10.0/BrainReloadSpike.dll
```

Exit code 0 when every expectation holds. `BRAINSPIKE_TIMEOUT_MS` (default 1500) is how long the
harness polls GC before calling an unload blocked.

- `Contracts/` is shared and stays in the default context (interfaces plus protobuf types).
- `Brain/` is the reloadable assembly; one small brain per hazard, `-leak` variants omit cleanup
  on purpose as negative controls.
- `Host/` is the loader and harness. Add a scenario by adding a case in `Brain/Brain.cs` and a row
  in `Scenarios.All`.
