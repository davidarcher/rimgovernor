# Native movement checks

Build `NativeMovementOperations.csproj`, then run its `net472` executable with
the private compiled bridge DLL and dependency directories (game managed
assemblies, RimBridgeServer SDK, Harmony, and private runtime if separate).
The tests load actual compiled adapters and generated Protobuf types.

Checks cover explicit coordinates and snapshot presence, exact draft ownership,
native pawn eligibility, queued orders, verified arrival, vanished jobs and
revocation. Run the draft operations, pawn control state, authority hooks and
operation envelope neighbors. Gameplay acceptance requires the private native
runner; these checks do not establish pawn movement.

The adapter requires an existing eligible owned draft and exact snapshot. It
refuses blocked, fogged and unreachable destinations without snapping to another
cell. A pawn already at the destination yields NoChange without a job setter.
Otherwise it issues ordinary native Goto under current authority. Only the exact
observed job and one native order revision establish correlation. Queued jobs
remain pending; disappearance alone cannot establish arrival. Native generation,
claim or later order changes invalidate movement progress.
