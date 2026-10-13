# Brain host

The brain host (`brain/src/RimGovernor.Brain.Host`, `BrainHost`) runs the C# brain inside the mod's process behind the
launch flag `-rimgovernor-inproc` (`InProcLaunch.Flag`). It is the controller that `cmd/rimgovernor serve` was, minus
the lease, blind-tick and reconnect machinery: the brain and the game share a process, so a link cannot drop under it.

## Lifecycle

`BrainHost.StartAsync(options, ct)` in order: validate options; start the OTLP file writer (a failure is reported and
the rows go nowhere, it never stops the brain); register the intent kinds (`BridgeFunctions.RegisterIntentKinds`, the
Go `init()`); open the state database (an incompatible one is moved aside); compose the session, the player and the
planners; create the Worker; register the player routes; start the save-signal loop, the auto-resumer and the
scheduler thread. A failure unwinds what was built and throws. `StopAsync` is idempotent and ends the loops, drains
the Worker (closing player and session, bounded retries), closes the database and flushes telemetry; errors are
collected and thrown together after everything else ran.

## Scheduler thread and authority

One dedicated thread (`rimgovernor-brain-scheduler`) steps the `ClockScheduler` with the Go `ClockWorker.stepLoop`
cadence: first step Full, delay doubling to the maximum on an unchanged step key, evidence resets it, a settled step
after a window. It idles unless the player is enabled with a known observation. Authority is the loaded world's:
`FacadeAuthority` implements `INativeAuthority` over the facade, and `AutoResumer` offers each fresh world one resume
through the same control intent the resume route submits.

## Fault isolation

Each step runs on the pool behind a guard: past `StallAfter` it is recorded as a stall, past `AbandonAfter` the thread
stops waiting and the brain is wedged. `FaultLimit` consecutive faults or any stall/wedge suspends authority
(`Session.Disable`), logs it, and keeps the game running; only the player's resume clears the suspension. Hazard stops
stay native and never wait on a planner. Planners that miss the wave cutoff `PlannerMissLimit` times in a row are
reported in `BrainHost.Status()`.

## Native client and composition (#3113)

`FacadeNativeClient` (`Client/`) is what Go's `bridge.Client` was to the planners, minus the transport (GABP, the ring,
leases, reconnect, blind ticks): one class over `INativeFacade` that implements the ~70 Native/Source interfaces the
planners are written against. Each Go `Client.ReadX` is one `ReadXAsync` core method (request, facade call, outcome
unwrap, the Go reply validators); the interfaces are bound to them in `FacadeNativeClient.Interfaces*.cs`, with
explicit implementations where the interface shape differs from the core method's. A failure or unavailable arm throws
`NativeFailureException` / `NativeUnavailableException`; a malformed reply throws the contract exception. Writes go
through `FacadeActionsWriter` (`IActionsWriter`) and `FacadeClockWriter` (`IClockWriter`). `CountingNativeFacade` wraps
the facade so every native round trip is counted into the attached `ReadTally` (`BridgeFunctions.CountNativeRead`, under
Go's `rimgovernor/*` tool names), which is how a scheduler step reports its reads.

`ClockWindowDriver` (#3108) is the window side of the clock: after each planning step it admits and starts the window,
settles owed epochs, re-takes the clock from the player and polls the native journal. The composition builds it with
`ClockWindowDriver.CreateAsync` over a `ClockCapabilities` session and an `IClockDriverNative`, wraps it in
`ClockWindowHostDriver` and returns it as `PlannerParts.Windows`. The composition (`startServiceClock` and
`roundsCapabilities`) is `IBrainComposition`; its family flags are all on by default and
`RIMGOVERNOR_ROUTINE_FAMILIES` narrows them, as in Go.

Not in the client: the providers, Save/Load/NewColony and autosave (#3114), the mod-side hosting (#3115), and the
catalog's Def-backed factory and facade label checks (#3116). The catalog reads need `CatalogFactory`, which supplies the
game's Defs and stat source; until the mod host installs it they throw.

## Watchdog thresholds

Go had no watchdog (a stuck step held `ClockWorker` until its step timeout), so `BrainWatchdog.Standard()` is a chosen
starting point, not a port:

| Threshold | Value | Why |
| --- | --- | --- |
| `StallAfter` | 90 s | A step may use its whole 1 min `StepTimeout` plus half again before it counts as a stall. |
| `AbandonAfter` | 30 s | A step that ignores its cancellation token this long after the stall is abandoned and authority suspended. |
| `FaultLimit` | 5 | Consecutive faulted or stalled steps before authority is suspended; five is a pattern, not a blip. |
| `FaultBackoff` | 5 s | Half the worker's maximum idle delay (10 s), so a fault does not spin and recovery stays prompt. |
| `PlannerMissLimit` | 3 | Consecutive missed wave cutoffs before a planner is reported stalled in `BrainHost.Status()`. |

They are unmeasured against a live game; tune them from the clock-step telemetry once the acceptance run (#3085) exists.

## Open decisions

What a suspended planner reports (today the whole brain is
suspended, since `ClockScheduler` cannot drop one planner); how the host is loaded in the game and whether it sits in a
collectible load context; the shared-assembly binding assumption from #3080; the profile/state path settings; how the
Bridge hands its `RouteTable` to the host.
