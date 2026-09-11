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
It also invokes the actual SDK argument binder against the production method and
its root dictionary normalizer against ProtoBoundary.Encode. Missing and malformed
raw values must reach boundary validation without SDK coercion. The binder is not
a full journal invocation. The sibling native-proto-boundary suite uses explicit
journal/game seams to check parsing and identity refusal. This suite creates no
Game and seeds no native definitions; game placement, stock scans and absence
of clock/camera/order changes require the separate native acceptance run.
