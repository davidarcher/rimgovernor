# Harness idle exit probe

Compiles the production `NativeOpTable.InvokeAsync` boundary and `IdleExit`
accounting with fake typed and registry operation bodies. A controlled monotonic
clock checks typed/fixture activity, overlapping requests, completion, synchronous
failure, cancellation and expiry after the harness stops calling. The gate runs it
as `native-idle-exit`.

No game is launched. The native build checks the actual HTTP table and shutdown
hook; a colony review beyond the prior idle cutoff supplies gameplay evidence.
