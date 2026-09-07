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

## Implementation

See [Visual architect](VISUAL_ARCHITECT.md) for the implemented survey, shared
reservations, native colored planning marks and execution checks. The library is
conditional guidance to that planner; geometry comes from the game, not the wiki.
