# Native facade, frame handoff and game-thread stat service (#3080)

The in-process brain reaches the native mod without GABP or JSON. This page is the contract for
that path; the history of the wire it replaces is `contracts/native-protobuf-cutover.md`.

## Assemblies

One set of wire records and one build graph. The native mod (`RimGovernor.Bridge`) references the brain
assemblies `RimGovernor.Brain.Domain`, `RimGovernor.Brain.Contracts` and `RimGovernor.Brain.NativeFacade`
by project reference. Contracts holds the plain C# records (`RimGovernor.Contracts.Wire`) and the
source-generated `System.Text.Json` context; nothing is generated at build time.

| Assembly | Holds | Depends on |
| --- | --- | --- |
| `RimGovernor.Brain.NativeFacade` | `INativeFacade`, `NativeFrame`, `FrameHandoff`, `NativeFacadeRegistry` | Domain (`IStatSource`), Contracts |
| `RimGovernor.Bridge` (mod) | `InProcessNativeFacade`, `GameThreadStatService`, the tools | all three, plus the game |

`scripts/build_native_mod.ps1` copies `brain/Directory.Build.props`, `brain/Directory.Packages.props` and the
three project directories into its private build copy and ships `RimGovernor.Brain.*.dll` beside the Bridge in
`BridgeTools/RimGovernor/`. `go/internal/nativeaccept/inputs/sources.go` lists the same inputs, so the
staleness hash (`sourceTree`) still reproduces the script's. Edit the two together.

The mod publishes its facade through a module initializer (`NativeFacadeBootstrap`), because the host loads the
assembly by file and gives plugins no startup hook. The brain reads `NativeFacadeRegistry.Current`.

Shared types must resolve from one place: the host's `AssemblyResolve` binds by simple name to an
assembly already loaded, so the first copy of `RimGovernor.Brain.NativeFacade` wins. Loading the brain in a
collectible context (#3084) has to keep these three in the default context. Not exercised in a running game yet.

## `INativeFacade`

Every native tool is one `...Async(request, CancellationToken)` call taking and returning the same record the
tool's JSON body carries. Nothing is serialized or parsed. A tool and the facade call the
same typed core (`internal static Handle*` on the tool class), so validation, refusals and replies are identical;
the tool only parses JSON before and encodes after.

- A call that touches game state runs on the game thread through `ProtoBoundary.OnGameThread`, which uses the
  tool path's admission class (`control` for writes, `observation` for reads), per-frame allowance, watchdog line
  and observation account. A facade call has no tool context, so it takes the host's game-thread access from
  `RimBridgeMainThreadAccess.Current`.
- Not on the game thread, as their tools: `WaitSaveSignalAsync`, `FlushDoneAsync`, `FlushFramesAsync`, and
  `PutGovernorStateBatchAsync` unless a save that predates the component needs it attached.
- Callers must not be on the game thread (they would wait on themselves).
- A refusal is the reply's `Failure` or `Unavailable` arm, exactly as before. A bug inside the hop faults the task.

## Frame handoff

`OpenFramesAsync` subscribes in process: `SnapshotStream` marks the handoff attached and captures on the same
cadence, pause edge and write triggers as before, but creates no shared-memory ring for the subscriber. The
encoder worker publishes each frame to `FrameHandoff`, a bounded channel of one with `DropOldest`: latest wins,
the game side never waits, a reader that falls behind sees only the newest frame.

Every handed-over frame is whole: sections the ring path elides (`SnapshotSections.Elide`) are all present and
the cell grid is a keyframe (`CellGridEncoder.AttachWhole`, which leaves the ring's delta baseline alone). When
the ring is also attached the frame is cloned before elision. A frame is immutable once published. The ring and
its tools stay until #3102 removes them.

## Game-thread stat service

`GameThreadStatService : IStatSource` (the production source `FakeStatSource` stands in for) answers the stat,
stuff and cost reads from the game. `INativeFacade.Stats` exposes it.

- Calls are synchronous for the brain and block the calling thread until the game thread drains them. Calling on
  the game thread throws `InvalidOperationException`. A call waits at most 30 s, then throws `StatException`.
- `Drain` runs once per frame from `ObservationFrameHook.OnUpdate` and answers all queued requests, so a planner
  pass asking for many stats is answered together.
- **Per-tick game-thread time budget: 1.5 ms per drain** (`GameThreadStatService.DrainBudgetMicros`). A drain
  stops starting new requests once it has spent that long; at least one request runs per drain and a request is
  never interrupted. The budget is separate from the main-thread admission allowance, since a drain takes no
  admission slot and no watchdog entry.
- Results are memoized per load (dropped when the drain sees a different `Current.Game`). Failures are never kept.
- No guessed default: an unknown def, stuff or stat, a non-finite value and a thing request carrying a stated
  `StatContext` (a hypothetical pawn the game cannot evaluate; the internal answer is Unknown) all throw
  `StatException`. Requests are built with `StatRequest.For(def, stuff, quality)` as `NativeStatTool` does, and a
  request with no quality returns the catalog's stat-table value (`GetStatValueAbstract`).
