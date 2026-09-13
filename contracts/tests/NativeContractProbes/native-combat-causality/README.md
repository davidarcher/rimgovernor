# Native combat causality checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged project, then run its net472
executable with:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-combat-causality ^
  <RimGovernor.Bridge.dll path> <private Runtime assembly directory> ^
  <RimBridge SDK assembly directory> <licensed Linux game managed assembly directory> ^
  <Harmony assembly directory>
```

1. The private `RimGovernor.Bridge.dll` path.
2. The private Runtime assembly directory.
3. The RimBridge SDK assembly directory.
4. The licensed Linux game managed assembly directory.
5. The Harmony assembly directory.

The executable loads the actual compiled Bridge, SDK, game and Harmony assemblies.
It exercises damage-result transitions and invokes the actual private prefix and
postfix/finalizers against explicitly constructed native object state. It checks exact
attacker/job/target/context identity, immutable job load IDs, main-thread refusal,
preexisting versus new downing, positive and invalid damage totals, concrete melee
verb scope, nested damage rejection, and exception-safe scope/depth restoration.
An active attack job alone supplies no attribution. Each of the five required
Harmony registrations is removed and restored; exact owner/method counts must
remain one, with a positive callback after each repair.

This is a standalone callback-state test, not game simulation. Uninitialized game,
map, pawn, health and job objects receive only the fields needed by these callbacks.
Unity startup and definition-binding flags suppress engine-only startup warnings;
no native behavior methods are replaced. The test installs the actual hook but
never calls `Thing.TakeDamage`, launches RimWorld, creates a listener, or issues an
order. Real damage, native damage-result correctness, combat job execution and
caller/receipt attribution require separate Docker gameplay acceptance.
