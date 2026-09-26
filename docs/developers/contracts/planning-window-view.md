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
| `published_tick` | When the root was published: the hop tick. |
| `complete` | The chunks tile the region's rows exactly, in order. |
| `chunks[]` | Row bands (`min_z..max_z`), each with its own `revision` (the publication that captured its content), `captured_tick` (when its cells were read) and `validated_tick` (the last tick the change grid confirmed them unchanged), cells in the `CompactCells` row format. |

Content revision, publication time and validation time are distinct: a
chunk carried over unchanged keeps its revision and capture tick and moves
only its validation tick. A view does not claim to describe the bundle
tick; its age is its oldest chunk's `validated_tick`.

## Native ownership

- The game thread captures a candidate root inside the bundle hop
  (`PlanningWindowViewCapture`): detached primitive cell values under the
  same visibility and absence rules as `observations_get_cells` (a fogged
  cell carries only its fog). A band of the current root whose every cell
  `CellTracking` shows unchanged since its capture is revalidated instead
  of read; every other band is read in full. The hop's observation account
  records `planningWindowView` with the cells actually read against the
  region's cells, which is the main-thread saving.
- The encoder worker publishes the candidate (`Interlocked` swap) and
  projects it to the wire; readers take the root once with `Volatile.Read`.
  Workers never touch game objects, `CellTracking` or live engine state.
- After construction nothing mutates a root, a chunk or their arrays;
  constructors copy what their builders hand them, and a revalidated chunk
  shares its predecessor's cell array read-only. Only the current root is
  retained and chunks keep no parent.
- A candidate publishes only when it is complete, from the current
  incarnation and newer than the current root. A superseded candidate of
  the current incarnation is still served to its own hop; one from an old
  incarnation is withdrawn. `Game.Dispose` and any identity change
  invalidate the view and every pending capture.
- Bounds: one slot, one region and mask per root, 4096 cells per root,
  bundle encoders (`ReplyEncoder`) plus a reader lease per projection.

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
- A stale, foreign or misplaced view falls through to the refresher's one
  bounded native read, exactly as without a view.
- The view is planning evidence only. Placement, target and authority
  validation at apply time (previews, CAS tokens) are unchanged.
