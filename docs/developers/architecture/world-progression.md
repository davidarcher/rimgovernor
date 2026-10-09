# World progression

[Documentation](../../README.md) · [Plans and Hands](plans-and-hands.md)

`Observations.ReadWorldProgression` is a scoped, read-only census of player caravans,
their pawn needs and inventory, active assembly lords and visible quest states.
World outcome predicates require later ticks, matching colony/load/map, exact
caravan membership and native terminal states. Missing map pawns, accepted
formation orders and accepted quests do not establish arrival or quest success.
Cold-weather readiness assessment uses stored-food forecasts, indoor sleeping,
actual sleeping temperatures and observed cold exposure. Future harvest and a
warm-weather shelter sample cannot establish winter readiness.

Caravan formation is a `FormCaravanIntent` on Actions/Apply: native checks
the crew, the cargo by definition (reserve stock first), a colonist left home,
the exit route, mass and a day of food when it applies, and a crew already
forming or travelling together is applied again. Applied means formation
started, not departure. Native scope checks reject old colony/load/map arguments.
Quest acceptance is an `AcceptQuestIntent` on Actions/Apply: native validates
eligibility and the explicit reward choice when it applies;
the acceptance action does not claim the quest objective is complete. Each
visible quest row carries `script_def`, the root QuestScriptDef name, so a
reader can tell a joiner offer (`ThreatReward_*_Joiner`) from a trade request
without parsing its parts. Hidden `WandererJoins` offers instead appear in the
colony census's typed `joiner_letters` section. MaintainPopulation answers their
native Accept option using the same population capacity policy; see
[population commitments](../contracts/population-contracts.md).

Once formed, a caravan travels under native control: nothing routes, holds or
recalls it, gifts its silver or fulfils a quest with its goods. EvaluateWorld
reports resource deficits and recovery needs from the census without issuing
orders. The census includes each active map and return routes to loaded home
maps.

The accepted formation contract covers free human colonists with explicit cargo,
leaves a colonist at home and requires at least one native food day. It refuses
unavailable crew, excessive mass and ineligible routes. Unsupported quest choice
structures require the ordinary quest interface. The bounded evaluation does not
establish every caravan composition, quest family or full-game survival.
