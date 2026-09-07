# Visual architect and shared reservations

The architect runs before construction projects enter native execution. Growing and storage specialists select their own zone cells without an architect handoff. It receives a
coordinate-labelled PNG plus lossless terrain runs, existing zones, construction,
approved projects and selected wiki guidance. It uses the configured planning
model without extra reasoning by default; the current target is Qwen 3.5 4B with vision support.

The typed result reserves filled footprints for rooms, pens and paths, while preserving existing zone reservations. Compatible projects can share one region. Interior storage/access can name
a containing room; unrelated overlaps are rejected. Zone specialists use exact native cells to exclude unsuitable holes. A region is either validated against the
survey or deferred with an explanation. Native crop rules remain authoritative.

Plans persist with the colony. New objectives can add sites without moving existing
reservations. Retiring all owners releases a site and removes unchanged native
planning marks; player-edited marks remain. This first iteration does not relocate
established reservations automatically. A newly conflicting player zone blocks
execution and is reported, rather than silently cleared.

Native named/color planning marks show the reserved footprint in-game. Interior
subregions appear on the dashboard because native plans cannot overlap. The Base
layout card shows the same coordinate system and the purpose of each reservation.
Planning marks are not construction orders or evidence of a finished enclosure.

Executors receive the shared layout. Native occupied-cell queries validate full
building dimensions/rotation. Beds cannot occupy a planned room boundary. Zones choose free surveyed cells while respecting shared land use. Room boundary batches must contain the
complete perimeter and an entrance, accounting for observed existing structures
and blueprints. Partial drafts can accumulate; incomplete final submissions are
returned to the model for correction in the same review. All spatial orders are
checked again before issue, including newly placed player zones.

## API and limits

Canonical OpenAPI generates the C# and Python wire types. Added native endpoints:
planning state/create/remove, construction footprints, and exact-cell growing
zones. Construction-area observations include zones, plans, doors and enclosure
cells. Native planning removal requires the expected cells and preserves edits.

The initial survey covers a 49-by-49 area around the observed colony focus. This is
a local site planner, not yet a whole-map architect or pathfinding proof. Region
connectivity does not prove pawn reachability. Pens reserve land but still require
native enclosure/nutrition verification by the responsible specialist. A complete
wall order is not proof of a roof, successful labor, or a finished usable room.
The architect currently preserves established layouts; broader explicit replanning
and phased remodeling are follow-up work.

## Verification

`controller_tests/test_spatial.py` exercises farm overlap, missing interiors,
irregular fertile footprints, compatible sharing, nested stockpiles, full building
footprints, incomplete perimeters and missing doors. `scripts/live_spatial_smoke.py`
uses the real local vision model and a disposable map with semantic objectives,
without supplied placement coordinates. It verifies native planning cells and
cleans up unchanged test-owned plans. It does not claim construction was completed.

## Initial planning and room sizes

The first Automate review pauses a running colony while strategy, proposals and the architect run, then resumes at normal speed before executor work. An already-paused colony stays paused. RIMAPI currently exposes pause state but not the previous selected speed, so this does not restore fast/ultrafast speed. Observed player unpausing releases the controller's pause ownership. The controller never resumes a different game session. This is an initial setup pause, not automatic tactical time management.

The architect always receives room-sizing guidance. Individual bedroom examples (5x5 or 4x6 interior) are distinguished from shared barracks and larger building envelopes. An 11x11 interior can be partitioned into four 5x5 interiors with cross walls, but door access/corridors must be planned separately; it is not a mandatory module. Native footprint checks remain authoritative during execution.

Live check on 2026-09-06: tick held at 9299 through initial planning, then advanced after the execution handoff. Architect chose 9x9 outer / 7x7 interior and created the native plan. Supply unforbidding was verified. This did not verify a completed shelter or a subdivided bedroom block.


## Specialist zone placement

Growing and storage execute before the construction architect turn. They choose exact cells from the local explored survey, using native crop minimum fertility and terrain facts. They need no preassigned site. Shared room perimeters, paths, and other project reservations still block incompatible placements; stockpiles may use room interiors. Fresh native zone occupancy is checked before every command, so a successful zone immediately protects its cells from subsequent placement. The native zone is the authoritative reservation, rather than a second cached copy of its coordinates.

Zone commands retain native legality checks and immediate readback. An architect failure blocks construction only. Regression tests cover unreserved placement, reservation conflicts, unsuitable soil, native zone overlap, and zone execution before an architect failure. A live stockpile smoke check verified creation, duplicate rejection, and removal; that is not a completed starter-base playtest.
