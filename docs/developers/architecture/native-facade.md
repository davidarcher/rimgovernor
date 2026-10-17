# Native facade and frame handoff (#3080)

The in-process brain reaches the native mod without a transport or JSON. This page is the contract
for that path.

## Assemblies

One set of wire records and one build graph. The native mod (`RimGovernor.Bridge`) references the brain
assemblies `RimGovernor.Brain.Domain`, `RimGovernor.Brain.Contracts` and `RimGovernor.Brain.NativeFacade`
by project reference. Contracts holds the plain C# records (`RimGovernor.Contracts.Wire`) and the
source-generated `System.Text.Json` context; nothing is generated at build time.

| Assembly | Holds | Depends on |
| --- | --- | --- |
| `RimGovernor.Brain.NativeFacade` | `INativeFacade`, `NativeFrame`, `FrameHandoff`, `NativeFacadeRegistry` | Contracts |
| `RimGovernor.Bridge` (mod) | `InProcessNativeFacade`, `NativeStatTable`, the typed cores | all three, plus the game |

The native build (`NativeModBuild`, `RimGovernor.cmd native-build`) copies the inputs
`brain/src/RimGovernor.Brain.Acceptance/CaseKit/NativeSourceInputs.cs` lists (among them `brain/Directory.Build.props`,
`brain/Directory.Packages.props` and the three project directories) into its private build copy and ships
`RimGovernor.Brain.*.dll` beside the Bridge in `BridgeTools/RimGovernor/`. The staleness hash (`sourceTree`) is the
hash of that copy, so a worktree reproduces it from the same list. The tools compute that hash
behind a fingerprint of each input's path, length and last-write time (rg's `SelfBuild` trust model): an unchanged
fingerprint returns the last hash from process memory or `.rimgovernor/source-tree.stamp` without reading contents,
so an edit that keeps both a file's size and its timestamp is not seen.

The mod has one startup: the `[ModuleInitializer]` `NativeStartup.Start` (Bridge, `Protocol/`). The host loads the
assembly by file and runs its module constructor on the game thread once play data has loaded, before it publishes
its registry. `Start` installs every patch and guard once, publishes the facade, and starts the HTTP channel and the
in-process brain. The brain reads `NativeFacadeRegistry.Current`.

Shared types must resolve from one place: the host's `AssemblyResolve` binds by simple name to an
assembly already loaded, so the first copy of `RimGovernor.Brain.NativeFacade` wins. A collectible brain
context (#3294) has to keep these three in the default context. The brain itself loads in the default context
today (see [brain host](brain-host.md)).

## `INativeFacade`

Every native operation is one `...Async(request, CancellationToken)` call taking and returning a wire record.
Nothing is serialized or parsed. The facade calls the typed core (`internal static Handle*` in the Bridge), and the
test-only `/native/{op}` route ([HTTP channel](http-channel.md)) calls the same facade method, so validation,
refusals and replies are identical.

- A call that touches game state runs on the game thread through `ProtoBoundary.OnGameThread`, which takes an
  admission class (`control` for writes, `observation` for reads), the per-frame allowance and a watchdog line, and the
  host's game-thread access from `RimBridgeMainThreadAccess.Current`.
- Not on the game thread: `WaitSaveSignalAsync`, `FlushDoneAsync`, `FlushFramesAsync`, and
  `PutGovernorStateBatchAsync` unless a save that predates the component needs it attached.
- Callers must not be on the game thread (they would wait on themselves).
- A refusal is the reply's `Failure` or `Unavailable` arm, exactly as before. A bug inside the hop faults the task.

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

## Def stat table

There is no stat call across the facade. The definition catalog read builds the load's immutable `DefStatTable` in
one game-thread pass (`NativeStatTable`), and the brain's catalog reads it on its own thread; see
[stat evaluation](../contracts/stat-classes.md).
