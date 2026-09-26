# Planning window view

[Subsystem contracts](README.md) · [Go clock recovery](go-clock-recovery.md)

The planning window view (#650) is the one maintained description of the
`planning_window_view` bundle section: the native publication, its
ownership rules and the controller's consumption. It is opt-in and separate
from the same-tick `planning_window` band, whose request, validation and
cache key are unchanged.

## Wire

`BundleRequest.planning_window_view` names a region of at most 4096 cells
(`BundlePlanningWindowViewRequest`); the fields are always the planning
fields (roof, visibility, traversal, zone, room, growth). The reply's
`BundleSnapshot.planning_window_view` (`PlanningWindowView`) carries:

| Field | Meaning |
| --- | --- |
| `context` | The serving hop's identity and tick; equal to the bundle context. |
| `region`, `applied_fields` | The region and field-mask key the root serves. |
| `incarnation` | Publisher incarnation, new on every unload and identity change (reload, rewind, another map). |
| `revision` | Publication revision, monotonic. |
| `published_tick` | When the root was published: the hop tick, or an earlier tick when the root is served while its successor is still being captured (#654). |
| `pending` | Set, with no chunks and `complete` false, when no complete root exists for the region yet: `capturing` (a frame-budgeted capture is running) or `saturated` (the capture queue or the reader slots are full). |
| `refreshing` | The served root is the last complete one; a newer capture of the region is running. |
| `complete` | The chunks tile the region's rows exactly, in order. |
| `chunks[]` | Row bands (`min_z..max_z`), each with its own `revision` (the publication that captured its content), `captured_tick` (when its cells were read) and `validated_tick` (the tick its content is known to match the map; see the dirty-chunk refresh), cells in the `CompactCells` row format. Chunk watermarks and the ledger generation (#652) stay native. |

Content revision, publication time and validation time are distinct: a
chunk carried over unchanged keeps its revision and capture tick and moves
only its validation tick. A view does not claim to describe the bundle
tick; its age is its oldest chunk's `validated_tick`.

## Native ownership

- The game thread captures a candidate root inside the bundle hop
  (`PlanningWindowViewCapture`): detached primitive cell values under the
  same visibility and absence rules as `observations_get_cells` (a fogged
  cell carries only its fog). Which bands it reads is the dirty-chunk
  refresh below (#652). The hop's observation account records
  `planningWindowView` with the cells actually read against the region's
  cells, which is the main-thread saving.
- The capture job publishes a finished root on the game thread
  (`Interlocked` swap, #654) and the encoder worker projects it to the wire; readers take the root once with `Volatile.Read`.
  Workers never touch game objects, `CellTracking`, the change ledger or
  live engine state.
- After construction nothing mutates a root, a chunk or their arrays;
  constructors copy what their builders hand them, and a carried-over chunk
  is the same object or shares its predecessor's cell array read-only. Only
  the current root is retained and chunks keep no parent.
- A candidate publishes only when it is complete, from the current
  incarnation and newer than the current root. A superseded candidate of
  the current incarnation is still served to its own hop; one from an old
  incarnation is withdrawn. `Game.Dispose` and any identity change
  invalidate the view and every pending capture.
- Bounds: one slot, one region and mask per root, 4096 cells per root,
  bundle encoders (`ReplyEncoder`) plus a reader lease per projection.

## Dirty-chunk refresh (#652)

`CellTracking` owns one `PlanningViewLedger` per map: map-fixed tiles of
8x8 cells, each holding the sequence number of the last mutation that
touched it. Its hooks mark in O(1) per cell and never read or allocate a
view. The sequence is a counter, not the game tick, so two edits in one
tick are distinct and a rewound tick cannot make an old mutation look new.
`MapFogged` is one broad revision and a room rebuild one topology
revision, not a loop. Each chunk records the ledger sequence its content
is current at (its watermark), read before any cell of the refresh, so a
mutation during or after a capture numbers past it and dirties the chunk
again whichever order the hops publish in.

`PlanningViewRefresh`, per eight-row band of the region, in order:

1. A root-level resync rebuilds every band: `bootstrap` (no current root,
   including after unload), `incarnation`, `region` (region or mask
   differs), `tracker` (the root was counted in another ledger: a new map
   object, a reload), `rewind` (the tick is before the root's
   publication) or `overflow` (more than 1024 distinct tiles went dirty
   between refreshes).
2. A band whose tiles, the whole map (broad) or room topology changed after
   its watermark is rebuilt (`dirtyChunks`, `topologyChunks`).
3. A band validated 250 ticks or more ago (`ValidateEveryTicks`, the
   controller's tightest planning tolerance) is scanned for the unhooked
   fields; unchanged it is revalidated at the tick, otherwise rebuilt.
4. Any other band is carried over as the same object; its
   `validated_tick` does not move.

So a chunk's content is always what the map held at its `validated_tick`:
hooked fields could not have changed since without rebuilding it, and
unhooked ones are compared at least every 250 ticks. Nothing is marked
fresh because no event arrived. A root the refresh cannot build (a read
throws, the tracker is unavailable) is no view for the hop, never a stale
one. The captured `room_id` is never reused across a room rebuild.

| Field | Invalidation |
| --- | --- |
| terrain, `supports_light` | `TerrainChanged` marks the cell. |
| `roof` | `RoofChanged` and the `RoofGrid.RemoveRoofUnsafe` patch mark the cell. |
| fog (`visibility`) | `CellFogChanged` marks the cell; `MapFogged` is broad. |
| `occupied`, `doorway` | Spawn/despawn of any edifice, blueprint or frame marks its rect. |
| `walkable`, passable | Those spawns plus anything with a path cost or non-standable passability, and `PathCostRecalculate`. |
| `zone_id` | The `ZoneManager.AddZoneGridCell` / `ClearZoneGridCell` patches mark the cell. |
| `storage_empty` | Spawn/despawn of items, plants, buildings, blueprints and frames. |
| `fertility` | Terrain and building spawns as above; pollution's effect via the scan. |
| `room_id` | `RegionsRoomsChanged` (every region/room rebuild; the capture forces a pending lazy rebuild first) is a topology revision: every band rebuilds. |
| `indoors` | Room rebuilds as `room_id`; a roof or role change far from the cell (no rebuild) by the 250-tick scan. |
| `glow` | No event (sky light moves every tick): the 250-tick scan. |
| `polluted` | No per-cell event wired: the 250-tick scan. |

Complexity: a refresh costs the tiles under each band (a handful per band)
plus the cells of dirty, topology-invalidated and expired bands, plus the
worker's projection of the whole root. A bootstrap, a resync, a room
rebuild or a map-wide change is O(region); the projection is always
O(region), so this is not O(changes) on the wire, only on the game
thread.

Telemetry (#642): the hop's `timing.observation.planningView` carries
`available`, `chunks`, `reused`, `validated`, `rebuilt`, `dirtyChunks`,
`topologyChunks`, `dirtyTiles`, `tilesScanned`, `cellsScanned`,
`cellsRead`, `ageTicks` (to the oldest band validation), `retainedBytes`
and `resync` when a root-level resync ran. Probe:
`native-planning-window-view` (ledger, refresh and publication over a
fake grid). Live parity: `cells/planning-view-refresh`.

## Frame-budgeted capture (#654)

The refresh is a resumable job (`PlanningViewRefreshJob`): the resync
decision and the dirty-list drain happen once when it starts; each unit
then judges and, when needed, reads one band at the tick it runs, reading
the ledger sequence as that band's watermark before any of its cells. An
edit between two units therefore numbers past the watermark of every band
already done (the next refresh rebuilds it) and is seen by every band
still to come. Chunks carry their own capture and validation ticks; the
root is published at the tick of its last unit.

- **Scheduler** (`ObservationScheduler`, `ObservationBudget.cs`): at most
  `MaxJobs` (4) optional jobs, run on the game thread from the real frame
  boundary (the `TickManager.TickManagerUpdate` prefix that drives the
  frame account) and inline from the bundle hop that wants the result.
  Both charge one per-frame allowance (`ObservationFrameBudget`), so the
  allowance is per frame, never per request. The allowance is
  `RIMGOVERNOR_OBSERVATION_BUDGET_MS` in [0.1, 50], default 1.5 ms.
- **Overrun**: a unit (one band, at most 8 rows of the region) is not
  preemptible. A unit starts only while allowance remains, so a frame
  overruns by at most the one unit in progress; `maxUnitMs` and
  `maxOverrunMs` measure it rather than pretending a stopwatch interrupts
  a native accessor.
- **Control first**: at the frame boundary, queued control hops
  (`MainThreadAdmission.RunControl`) run before every optional unit, and
  their cost is accounted apart (`controlServiced`, `controlMs`), never
  against the optional allowance. Inline in a hop, a queued control hop ends
  the hop's quanta instead, so the next pump serves it. Hazard and
  authority checks are unchanged and never wait on the allowance.
- **Progress and deadlines**: jobs run round-robin, and the boundary gives
  each frame at least one unit even with the allowance spent, so a job
  progresses under pressure; one unfinished after `DeadlineFrames` (600)
  frames is abandoned as `expired`.
- **Validity**: before every unit the job checks the publisher incarnation
  (unload, reload, identity change), that its map is still loaded, that
  the map's change ledger is the one it started on, and that the tick did
  not rewind; an obsolete job is discarded unpublished. No live game
  enumerator is held across units.
- **Coalescing and cancellation**: a request for the same identity and
  region joins the running job; a different region supersedes it. A
  caller's cancellation stops only its own inline quanta; the job carries
  on for the others. A failing unit discards its job.
- **Serving**: a bundle whose job finished (inline or on an earlier frame)
  serves that root. Otherwise it serves the last complete root for the
  same region, with `refreshing` set and its own ages, or `pending`:
  `capturing`, or `saturated` when the queue or reader slots are full. It
  never falls back to a synchronous full capture. The legacy same-tick
  `planning_window` band is unchanged and stays atomic.
- **Bounds**: 4 jobs, one root slot, one region-sized candidate per job
  (`retainedBytes`), bundle encoders plus a reader lease per projection.

Telemetry: every hop reply after the first unit carries
`timing.observationBudget` (`allowanceMs`, `frames`, `units`,
`aggregateMs`, `maxUnitMs`, `maxOverrunMs`, `overrunFrames`,
`deferredFrames`, `controlServiced`, `controlMs`, `pendingJobs`,
`expired`, `abandoned`, `saturated`); the hop's `planningView` account is
its job's cumulative work and `planningWindowView.rows` the cells that hop
read. Probe: `native-planning-window-view` drives the scheduler over a fake
frame source and clock and the job one band per frame.

## Controller consumption

- A review step whose planning cells are stale asks for the view of the
  held window's region instead of the legacy band
  (`ClockScheduler.bundleStepFamilies`).
- A native without the view refuses the request as invalid ProtoJSON. The
  scheduler then re-reads the step's bundle once with the legacy band and
  never asks for the view again in that process: the one explicit fallback.
- The bundle validator admits the section only when requested and under the
  bundle's context. `bridge.DecodePlanningWindowView` refuses a region or
  mask mismatch, an incomplete root, chunks that do not tile the rows, a
  chunk validated after publication or captured after validation, and any
  cell that fails the planning row rules; a refused view is logged and
  unused, never a failed bundle.
- The planning window refresher (`planningWindow`) serves a decoded view
  when it covers the asked region and its oldest validation is fresh under
  the step's `ReadValidity` inventory class (#624). It files the rows in
  `facts.Store` as of that oldest validation tick with source
  `rimgovernor/observations_read_bundle#planning_window_view`, and emits a
  `planning_window_view` event with the reused chunk count and age. It
  never seeds the step read cache's `observations_get_cells` key.
- A planner that panned past the view's slack (#706) is served the view's
  rows inside its region plus one native `get_cells` read per uncovered
  strip (at most four, together fewer cells than the region), filed as of
  the oldest of the view and those reads; the `planning_window_view`
  event carries `panned`, `view_cells` and `read_cells`. A strip read that
  fails, or a region the view does not overlap, falls through to the one
  full read.
- A `pending` view (#654) decodes to `bridge.ErrPlanningViewPending`: it is
  logged, the step reads the window as it would without a view (only when
  due), and the next step asks again; it is never a refusal, so the view
  stays enabled. A root published before the serving hop is accepted and
  aged by its own chunk ticks.
- A stale, foreign or misplaced view falls through to the refresher's one
  bounded native read, exactly as without a view.
- The view is planning evidence only. Placement, target and authority
  validation at apply time (previews, CAS tokens) are unchanged.
