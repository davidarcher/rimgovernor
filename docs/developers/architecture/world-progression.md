# World progression

[Documentation](../../README.md) · [Plans and Hands](plans-and-hands.md)

`home/world_progression` exposes a scoped, read-only census of player caravans,
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
started, not departure. Native scope checks reject old colony/load/map arguments. Route actions retain
exact caravan membership and wait for world arrival or living home-map return.
Quest acceptance is an `AcceptQuestIntent` on Actions/Apply: native validates
eligibility and the explicit reward choice when it applies;
the acceptance action does not claim the quest objective is complete. Each
visible quest row carries `script_def`, the root QuestScriptDef name, so a
reader can tell a joiner offer (`ThreatReward_*_Joiner`) from a trade request
without parsing its parts. Hidden `WandererJoins` offers instead appear in the
colony census's typed `joiner_letters` section. MaintainPopulation answers their
native Accept option using the same population capacity policy; see
[population commitments](../contracts/population-contracts.md).

Expedition policy records limits on travel estimates, food margins, seasonal
destination temperature, diplomatic relations, concurrent parties and remaining
home staff. Since #942 no departure path enforces them: a caravan departure
carries only native's own formation checks.
Native first-rot estimates produce a separate warning: food quantity alone does
not guarantee supplies after spoilage, and the first expiring stack does not mean
every carried food item expires then.
It deducts departing food from the home forecast. Explicit returns can retain food
and temperature warnings so a stranded party can attempt recovery. Unreachable
routes still refuse. EvaluateWorld reports resource deficits and recovery needs
without issuing orders. SetExpeditionPolicy changes only the specified limits.
Active population commitments also retain B22's food, doctor and warden capacity
checks after removing the proposed crew and cargo. A pawn still awaiting admission
or integration cannot depart under an unfinished population commitment.

HoldCaravan stops the exact observed party. RouteCaravan can visit a nonhostile
settlement or return to the current home. Optional return storage resources require
later native unloading and a corresponding increase in stored goods; arriving at
the map edge alone does not complete that contract. Storage preview cells are
accepting candidates, not guaranteed capacity or completed hauling.

GiftToSettlement spends explicitly requested carried silver through native gift
trading and observes goodwill. FulfillQuest invokes the enabled native trade-request
confirmation with eligible carried goods and waits for native quest success.
Both recheck player spending limits. Failed and expired objectives stay terminal.
The world census includes each active map and return routes to loaded home maps;
changing the current map invalidates pending orders scoped to the previous map.

The accepted formation contract covers free human colonists with explicit cargo,
leaves a colonist at home and requires at least one native food day. It refuses
unavailable crew, excessive mass and ineligible routes. Unsupported quest choice
structures require the ordinary quest interface. The bounded evaluation does not
establish every caravan composition, quest family or full-game survival.

