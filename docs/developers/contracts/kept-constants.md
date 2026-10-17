# Kept constants

[Documentation](../../README.md) · [Contracts](README.md)

Epic #2533 removed the caps and refusals that pre-vetoed what RimWorld or the brain
should decide. What remains is listed here once, each with the reason it stays
or the follow-up issue that owns it. A code comment at each constant points
back to this page. A cap that survives and can cut data tells the reader (see
[event detail](#visible-truncation)); a cap that does not belong gets an issue,
not a silent exemption.

## Kept, with the reason

| Constant | Where | Value | Why it stays |
| --- | --- | --- | --- |
| `ScheduleHours` | `WorkAssignment.cs`, `NativeWorkSettings.cs` | 24 | A game day is 24 hours; the timetable has one `TimeAssignmentDef` per hour. See [work assignment](work-assignment.md). |
| `domain.MaxPriority` | `WorkAssignment.cs` | 4 | The one range constant for priorities 0-4: RimWorld's manual work priorities are 1 (first) to 4 (last) with 0 disabled, and Standard/Incident priorities share the scale so a plan ranks against the work matrix. Domain validation, the work and apparel-policy readers and the gear loadout use it; the Concern-specific values (`artPriority`, `burialPriority`, `babyFeedingPriority`) are choices inside the range, not bounds. |
| `raidGiveUpTicks` | `CombatWait.cs` | 38000 | Humanoid raiders give up 26k-38k ticks after arriving (sappers 33k-38k); a raid still present past the window's end is not leaving and the fight re-forms. The vanilla window, not a tuning knob. |
| `CombatMirror.RingSize` | `CombatMirror.cs` | 1024 | The combat event ring; every frame carries it whole, and a reader works from the watermark. See [hazard detection bounds](../architecture/hazard-detection-bounds.md#combat-event-ring). |
| `CombatMirror.MaxDetailChars` | `CombatMirror.cs` | 256 | An event row's free-text `Detail`; a cut value ends in `...[truncated]` ([below](#visible-truncation)). |
| `InvalidationEntitiesMax` | `NativeClockRuntime.cs` | 64 | The narrowing of an observation invalidation: a larger entity list is an invalid scope, and the family-wide form is the way to name more. A message bound, not a game rule. |
| `clockInboxCapacity` / `clockInboxBytes`, `ReviewCapacity` | `ClockReview.cs` (`ReviewCapacity`) | 4096 / 16 MiB, 4096 | Persistence rings of the clock inbox and review log; their wrap and refusal behaviour is in [Clock recovery](clock-recovery.md). |
| Event read limit | `NativeClockTools.cs`, `ClockEventJournal.cs` | 1..128 | A page size for `clock_read_events`; the cursor makes the next page explicit. |
| Overlay bounds `MaxCells`, `MaxLabels`, `MaxLayerId` | `ProtoOverlayTools.cs` | 250000, 4096, 64 | Input bounds on an output-only draw request. |
| Placement candidates | `PlacementProtocol.cs` | 1..64 | Input bound on one placement preview/apply request, a main-thread budget. |
| Request id lists | room and supplies observation tools (256), zone observation `MaxIds` (16), quest shuttle explicit pawns (256) | 256 / 16 | The caller names exact ids; an input bound; larger sets are several requests. |
| `CombatGeometryTools` `MaxCells`, `MaxHostiles`, `MaxRadius` | `NativeCombatGeometryTools.cs` | 64, 16, 12 | Measured on `lab-ranged`: 64 cells x 8 pawns took 1.3-6.6 ms, about 13 ms at the caps. |
| Door and mortar frame caps | `SnapshotFrames.cs` | 64, 16 | Transport caps on a frame section; census #2641 kept them. |

## Forecast native reads: no cap, measured (#3341)

The forecast's native reads (planned sites, planned routes, needs, and the coupled read with its construction,
food, cooking, recipe, thermal, support, practice and hunt sub-reads) cap nothing: every colonist, candidate and
location pair is read. Each read searches a path once (`NativePathLegs`) and re-prices it per colonist. The
main-thread time on `observations/large-colony-reads` (32 free colonists, five planned rooms, twenty wood stacks,
eight meal stacks, eight beds; one lab map, 2026-10-16) is a measured fact, not a bound:

| Read (one call a round) | Cold ms | Warm ms | Size |
| --- | --- | --- | --- |
| Planned sites (`NativeConstructionActivity`, `Planned`) | 288 | 269 | 20 sites (largest 144 cells), 4420 rows, 1275 path searches |
| Planned routes (`NativeConstructionActivity`, `Routes`) | 98 | 92 | 311 probes, 9952 routes, 451 path searches |
| Needs (`NativeNeedsForecast`) | 55 | 45 | 32 colonists, 240 targets |
| Coupled (`NativeCoupledForecast`, needs passed in) | 104 | 99 | all sub-reads, 16 locations, 8192 routes |
| Round total | 546 | 506 | |

The round stalls the main thread about half a second on this colony. No limit is set from it; spreading a read
across frames is the remedy if the stall matters.

## Tracked in follow-up issues

| Constant | Where | Value | Issue |
| --- | --- | --- | --- |
| `HiveBoundaryCells` | `NativeObservationTools.cs` | 10 | #2652 (reads the game's constant) |

The native attempt ledger, pawn-control table and production bill tracking
have no count cap (#2662): each grows with the live game's pawns, bills or
admitted clock attempts and is dropped with the game.

## Visible truncation

A combat event row's `Detail` is cut to 256 characters. A cut value is the
first 242 characters followed by the marker `...[truncated]`, so the text is
exactly 256 characters and a reader recognises a cut value by the suffix. The
marker is in the text, not a field: no proto change.
