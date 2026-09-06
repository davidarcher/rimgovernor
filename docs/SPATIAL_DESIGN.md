# Spatial design guidance

Reviewed RimWorld Wiki on 2026-09-06. The library now includes `base-adjacency`,
`farm-footprints`, and `pen-layout`, alongside the existing shelter, kitchen,
food-storage, and crop entries. Normal bounded retrieval supplies relevant entries
to managers and task planners; it does not preload the whole wiki into context.

These are conditional strategies, not verified optimal layouts or authoritative
runtime rules. Use native definitions and live map observations for numeric rules,
mod-dependent behavior, crop eligibility, geometry and execution validation.

The preference for irregular farm footprints following suitable soil comes from
the player. The wiki supports comparing soil fertility, crop sensitivity, travel,
season and labor; it does not establish one universal best farm shape. Rich soil
far from camp is not automatically the best site.

Sources and checked dates are recorded with each library entry. Do not translate
suggested room dimensions or pen sizes into hardcoded recipes. Pen capacity needs
observed nutrition and animal demand, and construction needs complete geometry.

## Implementation boundary

The strategy entries are available to existing retrieval. They do not themselves
implement the shared spatial planner or prevent placement conflicts. That work
still needs native cell-level zone membership, one shared layout with reservations,
a visual architect, and validation before execution.

Inspection of the installed RimWorld assembly confirms `Designator_Plan_Add`
uses native `Plan`, `PlanManager`, and planning `ColorDef` entries. Named colored
in-game plans can therefore be exposed through RIMAPI without inventing a separate
rendering system. No planning API or visual architect is installed by this change.
