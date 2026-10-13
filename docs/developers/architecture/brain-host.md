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

## Seams (awaiting other work)

- `IBrainComposition`: the native client over the facade (the ~40 Native/Source interfaces Go's `bridge.Client`
  implemented) and the planner composition (`startServiceClock`/`roundsCapabilities`). Nobody owns it yet.
- `IClockWindowDriver`: the window side of the clock (#3108).
- `SessionParts.Orphans`: the trade orphan sweep.

## Open decisions

Watchdog numbers (`BrainWatchdog` has no defaults); what a suspended planner reports (today the whole brain is
suspended, since `ClockScheduler` cannot drop one planner); how the host is loaded in the game and whether it sits in a
collectible load context; the shared-assembly binding assumption from #3080; the profile/state path settings; how the
Bridge hands its `RouteTable` to the host.
