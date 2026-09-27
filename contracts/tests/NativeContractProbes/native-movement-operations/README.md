# Native movement checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged project, then run it with:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-movement-operations <args...>
```

where `<args...>` is the private compiled bridge DLL followed by dependency
directories (game managed assemblies, RimBridgeServer SDK, Harmony, and private
runtime if separate). The tests load actual compiled adapters and generated
Protobuf types.

Checks cover exact draft ownership and native pawn eligibility (`Owns`). Run the draft operations, pawn control state, authority hooks and
operation envelope neighbors (now other probe names within the same
`NativeContractProbes.csproj`). Gameplay acceptance requires the private native
runner; these checks do not establish pawn movement.

Movement is the `MoveIntent` arm of Actions/Apply (`MoveActionHandler`). Native
validates at apply time: the pawn must be alive, spawned and drafted under an
owned claim, and the cell standable, unfogged and reachable; it never snaps to
another cell. A pawn already on the cell or walking there is applied without a
new order; otherwise it issues an ordinary native Goto. The applied receipt is
terminal and says nothing about arrival.
