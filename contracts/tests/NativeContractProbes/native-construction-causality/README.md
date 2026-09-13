# Construction causality checks

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-construction-causality
```

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`.

These tests compile the production exact-reference transition helper. Equal-looking
objects with the same value equality deliberately cannot substitute for one another.
A completion requires one factory result, that exact object passed to and returned
by Spawn, predecessor destruction, and no native exception. A created but unspawned
object, an unrelated spawn, replacement result or ambiguous factory sequence remains
uncertain. Native scopes route only the innermost completion's observations.

The native adapter additionally checks spawned map/cell/rotation/faction/definition/
material against the admitted plan. Frame must be classified before Building because
Frame inherits Building. Blueprint replacement is observed after
`Blueprint.TryReplaceWithSolidThing`: `Blueprint_Build.MakeSolidThing` returns its
frame before faction, position, rotation and spawning are assigned.

Reviewed licensed Linux Assembly-CSharp.dll SHA-256:
`082db1dd4f7f1d0b72960d7e1beead8fbfe6957200e8627f65bda0dbbe1dd8f8`.
Actual `Frame.CompleteConstruction` and `FailConstruction` create the successor
with ThingMaker then pass that object to GenSpawn. The production hooks retain
those exact identities. Their failure remains uncertain; matching a replacement
at the same coordinate never establishes lineage.

Cancel observation requires the effective outer virtual Destroy override to return
without exception, mode Cancel, and the exact tracked object to be destroyed.
Base Destroy callbacks cannot certify success before derived/component callbacks
finish. Progress reports that attempt unsuccessful/cancelled, not desired-site
absence. Other unclassified destruction stays unknown.

Pure helper assertions and native compilation do not establish Harmony coverage or
actual pawn outcomes. Fresh gameplay acceptance must verify blueprint/frame/building
transitions, real cancellation and progress under the installed game runtime.
