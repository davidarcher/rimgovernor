# Kept constants

[Documentation](../../README.md) · [Contracts](README.md)

Epic #2533 removed the caps and refusals that pre-vetoed what RimWorld or Go
should decide. What remains is listed here once, each with the reason it stays
or the follow-up issue that owns it. A code comment at each constant points
back to this page. A cap that survives and can cut data tells the reader (see
[event detail](#visible-truncation)); a cap that does not belong gets an issue,
not a silent exemption.

## Kept, with the reason

| Constant | Where | Value | Why it stays |
| --- | --- | --- | --- |
| `ScheduleHours` | `go/internal/domain/work_assignment.go`, `NativeWorkSettings.cs` | 24 | A game day is 24 hours; the timetable has one `TimeAssignmentDef` per hour. See [work assignment](work-assignment.md). |
| `domain.MaxPriority` | `go/internal/domain/work_assignment.go` | 4 | The one range constant for priorities 0-4: RimWorld's manual work priorities are 1 (first) to 4 (last) with 0 disabled, and Standard/Project/Incident priorities share the scale so a plan ranks against the work matrix. Domain validation, the work and apparel-policy readers and the gear loadout use it; the Concern-specific values (`artPriority`, `burialPriority`, `babyFeedingPriority`) are choices inside the range, not bounds. |
| `raidGiveUpTicks` | `go/internal/policy/combat_wait.go` | 38000 | Humanoid raiders give up 26k-38k ticks after arriving (sappers 33k-38k); a raid still present past the window's end is not leaving and the fight re-forms. The vanilla window, not a tuning knob. |
| `StrangerTombStackCap` | `go/internal/policy/tomb.go` | 4 | `KnowBuriedInSarcophagus` is a four-deep stacking memory (4, 2, 1, 0.5); feeding strangers past four live stacks changes nothing. |
| `ReplyRing.Slots` | `ReplyRing.cs`; read by `go/internal/bridge/replyslots.go` | 8 | Fixed shared-memory reply ring, sized above the native encoder workers so an overwritten entry is a contract error rather than a retry. See [bridge hop](bridge-thrown-hop.md). |
| `CombatMirror.RingSize` | `CombatMirror.cs` | 1024 | The combat event ring; every frame carries it whole, and a reader works from the watermark. See [hazard detection bounds](../architecture/hazard-detection-bounds.md#combat-event-ring). |
| `CombatMirror.MaxDetailChars` | `CombatMirror.cs` | 256 | An event row's free-text `Detail`; a cut value ends in `...[truncated]` ([below](#visible-truncation)). |
| `UnackedRowsKept` | `SupervisedPlayRegulator.cs` | 256 | A backlog of unacknowledged rows this long is already held at Normal speed, so the tail need not be kept. |
| `InvalidationEntitiesMax` | `NativeClockRuntime.cs` | 64 | The narrowing of an observation invalidation: a larger entity list is an invalid scope, and the family-wide form is the way to name more. A message bound, not a game rule. |
| `clockInboxCapacity` / `clockInboxBytes`, `ReviewCapacity` | `go/internal/store/clock` | 4096 / 16 MiB, 4096 | Persistence rings of the clock inbox and review log; their wrap and refusal behaviour is in [Go clock recovery](go-clock-recovery.md). |
| Event read limit | `NativeClockTools.cs`, `ClockEventJournal.cs` | 1..128 | A page size for `clock_read_events`; the cursor makes the next page explicit. |
| Overlay bounds `MaxCells`, `MaxLabels`, `MaxLayerId` | `ProtoOverlayTools.cs` | 250000, 4096, 64 | Input bounds on an output-only draw request. |
| Placement candidates | `PlacementProtocol.cs` | 1..64 | Input bound on one placement preview/apply request, a main-thread budget. |
| Request id lists | room and supplies observation tools (256), zone observation `MaxIds` (16), quest shuttle explicit pawns (256) | 256 / 16 | The caller names exact ids; an input bound; larger sets are several requests. |
| `CombatGeometryTools` `MaxCells`, `MaxHostiles`, `MaxRadius` | `NativeCombatGeometryTools.cs` | 64, 16, 12 | Measured on `lab-ranged` against the 50 ms main-thread budget (64 cells x 8 pawns took 1.3-6.6 ms, about 13 ms at the caps). |
| Door and mortar frame caps | `SnapshotFrames.cs` | 64, 16 | Transport caps on a frame section; census #2641 kept them. |

## Tracked in follow-up issues

| Constant | Where | Value | Issue |
| --- | --- | --- | --- |
| Attempt ledger `Capacity` | `NativeAttemptLedger.cs` | 4096 | #2662 |
| Pawn-control table | `NativePawnControlState.cs` | 4096 | #2662 |
| Production bill tracking | `NativeProductionTracking.cs` | 4096 | #2662 |
| `HiveBoundaryCells` | `NativeObservationTools.cs` | 10 | #2652 (reads the game's constant) |

The three 4096 tables refuse new entries with a capacity failure; #2662
decides, per table, whether the cap goes or becomes a visible flag.

## Visible truncation

A combat event row's `Detail` is cut to 256 characters. A cut value is the
first 242 characters followed by the marker `...[truncated]`, so the text is
exactly 256 characters and a reader recognises a cut value by the suffix. The
marker is in the text, not a field: no proto change.
