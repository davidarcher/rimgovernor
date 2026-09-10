# World progression

[Documentation](../README.md) · [Plans and Hands](plans-and-hands.md)

`home/world_progression` exposes a scoped, read-only census of player caravans,
their pawn needs and inventory, active assembly lords and visible quest states.
World outcome predicates require later ticks, matching colony/load/map, exact
caravan membership and native terminal states. Missing map pawns, accepted
formation orders and accepted quests do not establish arrival or quest success.
Cold-weather readiness assessment uses stored-food forecasts, indoor sleeping,
actual sleeping temperatures and observed cold exposure. Future harvest and a
warm-weather shelter sample cannot establish winter readiness.

Explicit FormCaravan, RouteCaravan and AcceptQuest commands join the shared plan
and Hands. Caravan manifests reserve native cargo costs through assembly, even
after cancellation or an uncertain write, until loaded departure is observed.
Native scope checks reject old colony/load/map arguments. Route actions retain
exact caravan membership and wait for world arrival or living home-map return.
Quest acceptance validates native eligibility and an explicit reward choice;
the acceptance action does not claim the quest objective is complete.

The accepted formation contract covers free human colonists with explicit cargo,
leaves a colonist at home and requires at least one native food day. It refuses
unavailable crew, excessive mass and ineligible routes. Unsupported quest choice
structures require the ordinary quest interface. The bounded evaluation does not
establish every caravan composition, quest family or full-game survival.

