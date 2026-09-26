# Observation capture and frame accounting probe

Phase accounting for the observation capture path (#642), part of the
consolidated `NativeContractProbes.csproj`. It compiles the production
`ObservationWorkAccounting` and drives it with supplied clocks:
`FrameAccounting.UpdateAt` takes the monotonic timestamp and every
`ObservationWork` span takes a stopwatch tick count, so the intervals, the
slow-update counts at the declared thresholds and the millisecond conversions
are exact. No assertion depends on this machine finishing anything within a
deadline, and no game is launched.

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-observation-work
```

It covers: a hop's capture/format split with formatting passes counted apart
from their duration, the payload byte count being the envelope actually
returned, per-section rows and candidate totals (absent, not zero, where a
section has no candidate set), a hop that declares nothing reporting nothing,
the bounded section list, update-to-update intervals with the slow counts and
the most expensive observation's trace charged to the interval it ran in, the
bounded worst-interval ring keeping the widest rather than the newest, and a
new game session resetting the whole account.

It does not prove the game calls the patched `TickManagerUpdate` boundary
once per update, that
`ProtoBoundary` opens a scope around the real main-thread hop, or that a bundle
reply carries the block: those are native acceptance
(`speedmatrix/plain`, `observations/baseline`) and the phase report's own Go
tests.
