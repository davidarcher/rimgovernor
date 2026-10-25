# Compiled placement adapter checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the private production package with
`RimGovernor.cmd native-build`, then build the merged project in Release. Run it
with:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -c Release -- native-proto-placement <args...>
```

`<args...>` in order:

1. Private `BridgeTools/RimGovernor/RimGovernor.Bridge.dll` path.
2. Private package `Assemblies` directory.
3. RimGovernor.Host SDK assemblies directory.
4. Licensed RimWorld managed assemblies directory.
5. Harmony assemblies directory.

The executable loads the actual compiled adapter and the Contracts wire records.
It checks request presence and decoder errors (`System.Text.Json.JsonException`), explicit evaluated refusal, material
unavailability, complete geometry validation and oversized-response refusal.
The sibling native-game-thread probe checks identifiers and identity reads. This suite creates no
Game and seeds no native definitions; game placement, stock scans and absence
of clock/camera/order changes require the separate native acceptance run.
