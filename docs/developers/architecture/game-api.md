# Game API and frame handoff (#3080)

The in-process brain reaches the native mod without a transport or JSON. This page is the contract
for that path.

## Assemblies

One set of wire records and one build graph. The native mod (`RimGovernor.Bridge`) references the brain
assemblies `RimGovernor.Brain.Domain`, `RimGovernor.Brain.GameFacts`, `RimGovernor.Brain.GameOrders` and
`RimGovernor.Brain.Bridge` by project reference. GameFacts holds observed records and outcomes; GameOrders
holds requests and intent and can reference Facts. Each has a strict source-generated `System.Text.Json` context.

| Assembly | Holds | Depends on |
| --- | --- | --- |
| `RimGovernor.Brain.Bridge` | `IGameApi`, `NativeFrame`, `FrameHandoff`, `ColonyEventChannel`, `GameApiRegistry` | GameFacts, GameOrders |
| `RimGovernor.Bridge` (mod) | `GameApi`, `NativeStatTable`, the typed cores | the brain assemblies above, plus the game |

The native build (`NativeModBuild`, `RimGovernor.cmd native-build`) copies the inputs
`brain/src/RimGovernor.Brain.Acceptance/CaseKit/NativeSourceInputs.cs` lists (among them `brain/Directory.Build.props`,
`brain/Directory.Packages.props` and the project directories above) into its private build copy and ships
`RimGovernor.Brain.*.dll` beside the Bridge in `BridgeTools/RimGovernor/`. The staleness hash (`sourceTree`) is the
hash of that copy, so a worktree reproduces it from the same list. The tools compute that hash
behind a fingerprint of each input's path, length and last-write time (rg's `SelfBuild` trust model): an unchanged
fingerprint returns the last hash from process memory or `.rimgovernor/source-tree.stamp` without reading contents,
so an edit that keeps both a file's size and its timestamp is not seen.

The mod has one startup: the `[ModuleInitializer]` `NativeStartup.Start` (Bridge, `Protocol/`). The host loads the
assembly by file and runs its module constructor on the game thread once play data has loaded, before it publishes
its registry. `Start` installs every patch and guard once, publishes the game API, and starts the HTTP channel and the
in-process brain. The brain reads `GameApiRegistry.Current`.

Shared types must resolve from one place: the host's `AssemblyResolve` binds by simple name to an
assembly already loaded, so the first copy of `RimGovernor.Brain.Bridge` wins. A collectible brain
context (#3294) has to keep these three in the default context. The brain itself loads in the default context
today (see [brain host](brain-host.md)).

## `IGameApi`

Every native operation is one `...Async(request, CancellationToken)` call taking and returning a wire record.
Nothing is serialized or parsed. `GameApi` calls the typed core (`internal static Handle*` in the Bridge), and the
test-only `/native/{op}` route ([HTTP channel](http-channel.md)) calls the same API method, so validation,
refusals and replies are identical.

`GameClient` adapts those calls into the brain's source interfaces. `NativeOps` gives each operation one plain
name, such as `operations_apply`, shared by game-thread dispatch, read tallies, acceptance declarations and the
HTTP route. Discovery returns those exact registered names; there is no prefix translation or alias.
`GameAccess` owns the game-free read and action access models, while GameFacts owns native observations and outcomes and GameOrders owns native requests and intent.

- A call that touches game state runs on the game thread through `GameThread.OnGameThread`, which takes an
  queue class (`control` for writes, `observation` for reads), the per-frame allowance and a watchdog line, and the
  host's game-thread access from `NativeMainThreadAccess.Current`.
- A hop queued or running past the watchdog's 2 s logs a stall line that carries the game thread's breadcrumb: the phase
  it last entered (a hop, the frame prefix, the ticks, the frame postfix, a snapshot capture and the section it last
  finished) and how long ago. Once per process, a hop queued 20 s while the breadcrumb stays silent as long writes a
  minidump of the game with the `createdump.exe` beside the game executable, into `RIMGOVERNOR_OTLP_DIR` (the run
  folder); `RIMGOVERNOR_STALL_DUMP=0` turns it off and a test-acceleration launch never dumps (#3576).
- Not on the game thread: `WaitSaveSignalAsync`, `FlushDoneAsync`, `FlushFramesAsync`, `OpenColonyEventsAsync`, and
  `PutGovernorStateBatchAsync` unless a save that predates the component needs it attached.
- Callers must not be on the game thread (they would wait on themselves).
- The lifecycle, authority, clock and presentation reads (`ReadIdentity`, `ReadTick`, `ReadGovernorState`,
  `ReadAuthorityStatus`, `ReadClockStatus`, `RunClock`, `HoldClock`, `HeartbeatClock`, `ReadCamera`, `ReadSelection`,
  `ReadNotifications`, `ReadRenderState`) return their payload. A refusal throws `NativeFailureException` (a
  `BridgeRefusedException`) and an unavailable game `BridgeUnavailableException` (`NotLoadedException` when no game
  is loaded), built by `NativeFaults`. `ApplyAsync` keeps its typed refusal result
  ([interface contracts](../contracts/interface-contracts.md#game-api-refusals)); a family-owned read still
  answers its reply's `Failure` or `Unavailable` arm. A bug inside the hop faults the task.

## Frame handoff

`OpenFramesAsync` subscribes in process: `SnapshotStream` marks the handoff attached and captures on the same
cadence, pause edge and write triggers. A
worker publishes each frame to `FrameHandoff`, a bounded channel of one with `DropOldest`: latest wins,
the game side never waits, a reader that falls behind sees only the newest frame.

Every handed-over frame is whole: every section is present and the cell grid covers the whole map, read on the
game thread as one typed grid (`CellGridReader`: an array per cell fact, thing records per cell). A frame is
immutable once published. The acceptance harness pulls the newest frame
over `POST /native/observations_read_snapshot` (`SnapshotStream.Read`), which subscribes the same capture
schedule; there is no shared-memory ring.

## Colony event handoff

`OpenColonyEventsAsync` opens the supervisor's `ColonyEventChannel` in process and returns its reader; it has no
`NativeOps` entry and no `/native` route. Native builds each colony event as a plain record (`ColonyEvents.cs`) and
writes it with a non-blocking `TryWrite` on the game thread into a queue of 1024; a write that finds it full is dropped
and counted in `ColonyEventsLost`. The open drops what is queued and clears the counter, so the brain starts empty and plans its first
step Full; any later change in the counter makes the next step Full too.

## Def stat table

There is no stat call across the game API. The definition catalog read builds the load's immutable `DefStatTable` in
one game-thread pass (`NativeStatTable`), and the brain's catalog reads it on its own thread; see
[stat evaluation](../contracts/stat-classes.md).
