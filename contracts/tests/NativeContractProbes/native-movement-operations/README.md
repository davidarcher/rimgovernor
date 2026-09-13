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

Checks cover explicit coordinates and snapshot presence, exact draft ownership,
native pawn eligibility, queued orders, verified arrival, vanished jobs and
revocation. Run the draft operations, pawn control state, authority hooks and
operation envelope neighbors (now other probe names within the same
`NativeContractProbes.csproj`). Gameplay acceptance requires the private native
runner; these checks do not establish pawn movement.

The adapter requires an existing eligible owned draft and exact snapshot. It
refuses blocked, fogged and unreachable destinations without snapping to another
cell. A pawn already at the destination yields NoChange without a job setter.
Otherwise it issues ordinary native Goto under current authority. Only the exact
observed job and one native order revision establish correlation. Queued jobs
remain pending; disappearance alone cannot establish arrival. Native generation,
claim or later order changes invalidate movement progress.
