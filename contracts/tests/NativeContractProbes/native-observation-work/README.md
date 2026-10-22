# Observation capture span probe

The frame capture's section spans, part of the consolidated
`NativeContractProbes.csproj`. It compiles the production
`ObservationWorkAccounting` and drives `ObservationWork` with supplied
stopwatch tick counts, so no assertion depends on this machine finishing
anything within a deadline, and no game is launched.

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-observation-work
```

It covers: a repeated section accumulating ticks and rows, a detail span
marked apart from the families, negative spans clamped, recording outside a
scope dropped, a failed section's error row carrying the stack, and the
bounded section list. It also checks required building outcomes and the production
construction-work conversion: a final work increment that overshoots the total
leaves zero remaining labor, while nonfinite values are rejected. This arithmetic
probe does not run a native completion guard or prove colony-level recovery.

It does not prove that `SnapshotStream` opens a scope around the real
capture or that the slow-capture line reaches the log: that is native
acceptance (`lab/capture-profile-250` reads the same spans).
