# Persistent base architecture

The previous `spatial.prepare_layout` asked the visual model for exact regions
whenever a project-list signature changed. Regions were tied to project IDs and
released with project retirement. The same visual turn mixed long-term land use
with exact current room geometry.

The refactor keeps the existing `spatial_layout` persistence key, renderer,
construction executor, native planning marks and construction APIs. Its master
plan now contains versioned semantic zones, maximum reserved extents, corridors,
defensive lines, adjacency preferences, build phases, deviations and current
construction intents. `base_plan.PlanChange` is the architect submission schema.
There is one architect path; the old signature-triggered JIT model call is removed.

`prepare_layout` observes the local map and invokes the architect initially or
for an explicit material review. Population thresholds and changes to reserved
terrain/native zones generate review requests. Research/loss events can request
review; specialists can request technology, congestion, defensive, strategic or
blocked-expansion review with affected zone IDs. Merely adding a project is not a
trigger. Partial changes merge only affected zones, preserving other zones and
committed footprints. A local review cannot request a full redesign. NO_CHANGE
is explicit and recorded; previous deviations remain in history.

The existing executor acts as ConstructionManager. `reserve_planned_site` selects
a compatible semantic zone and current dimensions. Deterministic code finds a
surveyed footprint, keeps it within the maximum extent, excludes other commitments,
and checks observed access. Reusing a region can share it or enlarge it while
preserving its old cells. No buildings are created by reservation. Future maximum
extents do not become wall blueprints. Build phases supply context, never an
instruction to build every future facility. Approved current projects remain the
execution authority.

Every actual construction order still goes through native footprint and placement
inspection, research/constructor eligibility, material-definition validation and
revision checks. Room walls use the existing enclosure compiler. Shared validation
also protects corridors and future reservations from zone orders. Native reach and
construction checks remain authoritative; the observed walkability check is not a
complete future pathfinding simulation. No mining, demolition, resource spawning or
construction completion is implicit.

The existing map preview overlays future reservations, corridors, defense space
and current footprints. Native planning marks show current footprints; the dashboard
shows the larger master plan. The architect receives that overlay plus the persisted
plan, exact compressed terrain runs and current construction/resource facts.

Events use the existing `spatial_plan` activity channel with structured event names:
creation/load, invocation and triggers, changed zone IDs/version, deviations,
reserved-space conflicts and selected current increments. Per-tile logs are avoided.

Persistence follows the controller's current session boundary: controller restart
retains the plan for the same game session. Loading a RimWorld save creates a new
native session and resets AI history, as before. This change does not infer save
identity from map seeds or carry later AI state into an earlier save. The tribal
benchmark intentionally reloads its baseline with isolated fresh controller state.

Regression tests cover initial persistence, routine reuse, incremental expansion,
future-space conflicts, deterministic geometry rejection, localized review,
unrelated-zone preservation and deviation retention. These are controller tests;
a complete autonomous multi-day starter colony is a separate gameplay milestone.

`survey_master_area` combines four native bounded area reads into a wider local
view for the master plan. Unknown holes remain unknown. Native point/footprint
validation is unchanged. `scripts/live_master_plan.py` exercises the real local
model and checks that routine footprint growth does not invoke it again.
