# Colony extent contract

The colony extent is the controller's one shared territory model (#453,
milestone 2 of #338): where the colony *is*, recorded with why each cell
belongs, kept per saved timeline, and read by layout and resource policies
through one bounded query. It is distinct from native Home coverage (what
the game maintains and cleans) and from resource-operation reach (where
current readiness permits considering remote work). Territory history never
implies that a cell or route is safe today.

## Ownership

| Concept | Owner | Code |
| --- | --- | --- |
| Current extent | Pure policy derivation from held colony facts; no writer | `policy.DeriveColonyExtent` |
| Established history and expansion areas | Controller durable store, append-only per world and timeline | `store.EstablishColonyExtent`, `AddExpansionArea`, `RemoveExpansionArea`, `EstablishedColonyExtent`, `ExpansionAreas` |
| Eligibility | Pure overlay of current evidence on history; read-only diagnostics | `policy.ExtentEligibility` |
| Bounded consumer query | Pure geometry over extent plus areas | `policy.ExtentWindow` |
| Native Home mask | The game, through the guarded `ExtendHome` action only | [Maintained jobs](upkeep-contracts.md#maintained-jobs) |

Nothing in this contract writes native state. The extent is Go-side
knowledge about the colony; the game holds no copy of it.

## Derivation

`policy.DeriveColonyExtent` joins the complete `CurrentConstruction`
player-faction building census (#475), optional action provenance
(`ConstructionClaims`), exact owned stockpile footprints and each target's
complete `HomeCoverageTarget.ExtentGeometry` (enclosed roofed interior and
observed corridor cells, same geometry rules as Home) into sorted
four-neighbour regions. Every cell carries provenance: `facility`,
`enclosed_interior`, `corridor` or `margin`, with the facility identity and,
when known, the plan, action and goal that built it. A margin is an explicit
0–8 cell Chebyshev radius clipped to map bounds and belonging to its own
region; overlapping margins never merge regions. No bounding rectangle or
inferred path fills the gap between facilities: a wall fragment or a distant
Home island owns nothing between it and the base.

Identical facts produce an identical extent. Missing bounds, census,
target or complete geometry, a target with a blocker, or a building or zone
without a footprint keep the extent **unknown**, never empty; unknown is not
a permission and not a known-empty colony. Invalid geometry (out-of-bounds
cells, interior islands not connected to their footprint, ambiguous
provenance) is an error. Details: [colony upkeep
contracts](upkeep-contracts.md#maintained-jobs).

## Persistence and recovery (slice B, #517)

Established regions and explicitly selected expansion areas are an
append-only journal scoped to the world (colony, map) and to the saved
timeline the current load belongs to. A load token is one timeline segment
forking from the segment whose observed span covered its first tick;
loading an older save restores exactly what that timeline had recorded by
that tick, territory established later or on another branch never leaks
back, and another colony or map starts empty. Removal of an expansion area
is a later entry, never an edit. See [persistence
contracts](persistence-contracts.md#what-must-survive).

Established history may only grow. What shrinks is the *active* view:
facilities lost, threats present, routes unobserved. Historical Home
exclusions are not recorded: Home removals are current restorable state
that autonomous play overwrites, not player vetoes.

## Eligibility (slice C, #518)

`policy.ExtentEligibility` overlays same-world current evidence on the
established history without mutating it. Each region reports its stage,
origins, recorded and active facility identities and every hold:
`threat_present`, `threat_unknown`, `facility_lost`, `facilities_unknown`,
`provenance_unknown`, `route_unknown`, `route_impassable`, `extent_empty`.
A map-wide threat holds every region; a replacement facility cannot inherit
a lost one's provenance; missing evidence is a hold, never a grant. The
routines API `extentEligibility` and the dashboard's Colony extent view
render it; the acceptance harness records it for `acceptance why` and
postmortems.

## Consumer contract

A layout or site-selection consumer reads candidate area from the shared
extent, not from its own per-facility derivation:

1. Ask `policy.ExtentWindow` for a bounded rectangle: the extent (current
   derivation or established history) plus expansion areas give a bounding
   box; the window of the consumer's half-width is centred on it, shifted
   only as far as keeps the consumer's focus cell (a planner's Home cell)
   inside, then clipped to the map. The result names its source: `extent`
   when the shared extent anchored it, `focus` when the extent was unknown
   or empty and the consumer's previous focus-centred derivation applies.
2. Read observations inside that window through the consumer's existing
   native read; the window chooses *which cells to read*, nothing more.
3. Plan from those observations as before. The window grants no
   eligibility: current safety, admission previews, access audits and the
   consumer's own gates are unchanged, and an unknown extent narrows a
   consumer to what it already had rather than widening it.

The first consumer is the defense layout planner
(`buildingruntime.defenseRegion`): its 45 × 45 census rectangle, which used
to drift with the colonists' centre, is now the extent window around the
established footprint with Home kept inside, and it logs
`defense-layout: census window from colony extent` when the extent anchors
it. With no complete `ExtentGeometry` producer in the runtime yet the extent
is unknown on every existing fixture, so the planner's census region, and
therefore its plan, are byte-identical to the previous derivation; the
intended difference appears only once geometry is observed, when the
window anchors on the base instead of on wandering colonists.

The stone-shell and storage site planners still derive their candidate
area per facility (claims, starter room, the planning-cell window);
adopting the extent window there follows the same three steps.

## Home mask rule

Extent growth alone never changes the native Home mask. Establishing a
region, adding or removing an expansion area, and widening a consumer's
window are Go-side records; the only path that paints Home is the existing
guarded `ExtendHome` action, driven by the per-facility Home coverage
review (#452, #461), whose targets and batches do not read the extent.
Home coverage over a corridor between two controller-owned facilities is
therefore produced, and restored, by that path whether or not the corridor
is inside the extent, and an expansion area over open ground paints
nothing. The converse also holds: Home cells are an *input* to derivation
(through the coverage targets' geometry), never a consumer of it.

## Related reading

- [Colony upkeep contracts](upkeep-contracts.md): Home ownership, the
  derivation's inputs, resource reach and the eligibility view.
- [Persistence contracts](persistence-contracts.md#what-must-survive):
  extent history and timeline reconciliation.
- [Spatial contracts](spatial-contracts.md): site selection and bounded
  connectivity that consumers keep using inside the window.
