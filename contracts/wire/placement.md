# Placement preview contract

`placement.Placement.Preview` supplies the construction candidate/site facts used by ordinary placement. `operations.Zones.Preview` checks
zone sites. Full player single-placement thermal
inspection belongs the observation family; preview never changes camera, watches
or native orders.

The request requires exact current identity and 1–16 candidates. Each candidate
requires a current native buildable definition, x/z map coordinates, a cardinal
rotation or `ALL`, a stuff name and the ignore-natural-rock flag. Every field is
required; an empty stuff requests the native default material;
an explicit material must be valid for the definition. The native map bounds,
blueprint availability, ordinary buildability, material compatibility and research
checks remain authoritative. `ALL` is preview-only and produces each of four
cardinal rotations once; writes always name one cardinal rotation.

The batch carries `common.ObservationContext`, including int64 observed tick, and
one result per input in the same order. A candidate failure means evaluation could
not produce complete required facts; an evaluated `can_place=false` is a native
placement refusal. Every field of an evaluated candidate is required and read whole. An evaluated
candidate guarantees its complete native cost scan and every returned rotation's
complete footprint and blocker scan. Empty cost/blocker lists therefore mean
whole and empty. A failed or oversized scan fails the candidate rather than emitting
an empty list. Limits are 256 costs, 4096 footprint cells and 4096 blockers per
rotation; diagnostics do not replace exact facts.

Every rotation reports `watch_cells`, one of `not_watched` (no recreation watches the
definition), `reachable` or `unreachable`. It uses the native stand-distance, room and
sight-line geometry plus allowed-area and safe reachability for every eligible colonist.
Comfort planning requires `reachable` for horseshoes construction; ordinary placement
legality remains unchanged. This is potential access, not a receipt or proof of
actual recreation, and does not reserve land outside the building footprint.

Every rotation also reports `wind`, the shared building wind state: `not_wind_turbine` for
every other definition, or `wind_turbine` with the count of roofed, off-map or
wind-blocked cells of the native catch zone. A turbine site is clear only at zero.

Materials carries one row per material definition relevant to that candidate, without
truncation, each with its net available count; zero is empty stock. A scan that cannot
be completed fails the candidate, so it never masquerades as empty stock or authorizes
a write based on assumed resources. Placement operations use the normal game's resource rules regardless
of a preceding preview.

Available material is net stock: eligible spawned stacks minus forbidden stock
and the complete native census of remaining blueprint/frame material deficits,
clamped to zero. It is not gross inventory. The brain additionally reserves unissued or
unverifiable shared work. A complete correlated pending-construction inspection
lets stock from a later tick replace that action's historical material hold;
its footprint remains reserved until outcome reconciliation permits release.
