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

Explicit FormCaravan, RouteCaravan and AcceptQuest commands join the shared plan
and Hands. Caravan manifests reserve native cargo costs through assembly, even
after cancellation or an uncertain write, until loaded departure is observed.
Native scope checks reject old colony/load/map arguments. Route actions retain
exact caravan membership and wait for world arrival or living home-map return.
Quest acceptance validates native eligibility and an explicit reward choice;
the acceptance action does not claim the quest objective is complete. Each
visible quest row carries `script_def`, the root QuestScriptDef name, so a
reader can tell a joiner offer (`ThreatReward_*_Joiner`) from a trade request
without parsing its parts.

Expedition policy checks native travel estimates, food margins, seasonal destination
temperature, diplomatic relations, concurrent parties and remaining home staff.
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

