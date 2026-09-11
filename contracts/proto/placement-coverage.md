# Placement preview contract

`placement.Placement.Preview` covers `home/placement_previews` and the construction
candidate/site facts used by ordinary placement. `operations.Preview` supplies
other operation-specific preparations. Full player single-placement thermal
inspection belongs the observation family; preview never changes camera, watches
or native orders.

The request requires exact current identity and 1–16 candidates. Each candidate
requires a current native buildable definition, x/z map coordinates and a cardinal
rotation or `ALL`. Missing/empty stuff requests the native default material;
an explicit material must be valid for the definition. The native map bounds,
blueprint availability, ordinary buildability, material compatibility and research
checks remain authoritative. `ALL` is preview-only and produces each of four
cardinal rotations once; writes always name one cardinal rotation.

The batch carries `common.ObservationContext`, including int64 observed tick, and
one result per input in the same order. A candidate failure means evaluation could
not produce complete required facts; an evaluated `can_place=false` is a native
placement refusal. Required booleans and enums retain presence. An evaluated
candidate guarantees its complete native cost scan and every returned rotation's
complete footprint and blocker scan. Empty cost/blocker lists therefore mean
known empty. A failed or oversized scan fails the candidate rather than emitting
an empty list. Limits are 256 costs, 4096 footprint cells and 4096 blockers per
rotation; diagnostics do not replace exact facts.

Materials selects `known` or `unavailable`. A known list covers every material
definition relevant to that candidate without truncation (at most256 rows).
Within a known definition row, absent available means its count is unknown and
present zero means known empty. An unavailable enumeration never masquerades as
known empty stock. An unavailable materials scan cannot authorize a write based
on assumed resources. Reply size is at most1MiB; reject oversized read results
explicitly. Placement operations use the normal game's resource rules regardless
of a preceding preview.

Sources: `integrations/rimgovernor-native/src/Bridge/PlacementPreviewsTool.cs`,
`PlaceBuildingTool.cs`, and the typed `PlacementPreviewOperation` workstream
implementation; consumers are Go placement/domain transport and controller
construction preview callers. Native adapters still need fresh valid/refused/
invalid-definition acceptance, unchanged tick/identity/camera/order checks and
truthful complete scans. Official Protobuf replaces the experimental JSON wire
format; no parser-spelling or old-save parity is required.
