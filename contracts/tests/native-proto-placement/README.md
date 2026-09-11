# Compiled placement adapter checks

Build the private production package with `scripts/build_native_mod.ps1`, then
build `NativeProtoPlacement.csproj` in Release. Run the resulting net472 executable
with these arguments:

1. Private `BridgeTools/RimGovernor/RimGovernor.Bridge.dll` path.
2. Private package `Assemblies` directory.
3. RimBridgeServer SDK assemblies directory.
4. Licensed RimWorld managed assemblies directory.
5. Harmony assemblies directory.

The executable loads the actual compiled adapter and official generated messages.
It checks request presence and parser errors, explicit evaluated refusal, material
unavailability, complete geometry validation and oversized-response refusal.
It creates no Game and seeds no native definitions. SDK binder/envelope checks are
in the sibling native-proto-boundary suite; game placement, stock scans and absence
of clock/camera/order changes require the separate native acceptance run.
