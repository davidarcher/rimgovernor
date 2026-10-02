# Ref index checks

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-ref-index
```

Compiles the production `LoadIdIndex` behind `RefIndex` (#1339) and drives it the way
`RefIndex` wires it per map: a spawned thing, the inner thing of a spawned minified thing,
a zone and a bill on a spawned giver resolve by loadId. A live hit needs no rebuild; a miss
rebuilds, so a thing spawned since the last rebuild is found; a despawned thing, a bill on a
despawned giver, an unpacked thing and a deleted zone stop resolving. The Verse wiring itself
(`listerThings`, `zoneManager`, `BillStack`) is compile-checked with the mod, not exercised here.
