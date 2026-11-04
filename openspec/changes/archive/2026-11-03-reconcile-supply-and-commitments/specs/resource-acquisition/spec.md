## MODIFIED Requirements

### Requirement: RG-ACQUIRE-001 — Compare combined contributions and shared costs

The governor SHALL compare acquisition options across colony goals, crediting all
useful outputs while charging shared work and resources once. Selection SHALL
account for existing stock, commitments, competing resources, time to usable
delivery and risk. Food demand SHALL participate in that shared comparison.
Extraction of deposits on the colony map is a source in that comparison.

#### Scenario: A hunt serves leather demand when food is sufficient

- **WHEN** leather is needed and hunting is an available source although meat is plentiful
- **THEN** the hunt is eligible for comparison based on useful leather and any useful meat
- **AND** the hunt's shared costs are charged once rather than separately for each output

#### Scenario: Crops compete for a common production budget

- **WHEN** food, animal feed and clothing crops compete for land and labor
- **THEN** their contributions to colony goals are compared against the same available resources
- **AND** the same field capacity or worker time is not promised to multiple incompatible uses

#### Scenario: Expected loss justifies committing more than the point estimate

- **WHEN** expected loss from weather, raids, illness or abandonment makes the minimal plan likely to fall short
- **THEN** the governor may commit more capacity than the point estimate requires, with each committed resource promised to exactly one use
- **AND** the expected surplus is accounted for as reserve or trade stock rather than as uncounted slack

#### Scenario: Acquisition has not delivered

- **WHEN** work is planned or issued but its useful output has not reached the required destination
- **THEN** expected supply remains distinct from available inventory
- **AND** outstanding commitments remain accounted for without double-counting their output

#### Scenario: A chosen source's value is decomposable by demand

- **WHEN** a source is selected because it serves more than one colony demand
- **THEN** the governor can state how much of the source's credited value came from each demand
- **AND** the explanation uses the same shared comparison that selected the source rather than a separate account

#### Scenario: A deposit on the colony map depletes or needs prerequisites

- **WHEN** a surface vein or deep deposit could supply a material, or a worked deposit is exhausted or unreachable
- **THEN** the deposit's yield counts as expected supply only until ore is observed in stock, and an exhausted or unreachable deposit prompts a fresh source assessment rather than persistence
- **AND** research, scanning and drilling a deep deposit requires are costed into that option, and mining away from the colony map is not an option

### Requirement: RG-ACQUIRE-002 — Treat trade as an optional fully costed source

The governor SHALL consider trade in any goods, with reachable traders or by a
dedicated expedition, when its expected contribution justifies full costs and
risks. Expedition costs include travel time, provisions, carrying capacity,
danger and each participant's absence from work and defense, priced by the
capabilities that lose them; an expedition serves only a shortfall the forecast
places later than its return. Purchases and sales SHALL account for future needs
and justified reserves.

#### Scenario: An accessible trader offers useful food

- **WHEN** a trader offers food that could improve colony supply
- **THEN** the purchase is compared with other uses of the resources and other food sources
- **AND** the trader's existence alone does not require a purchase

#### Scenario: Food bought away from home is not yet home supply

- **WHEN** a caravan already away for another purpose buys food from a trader
- **THEN** the purchase is compared using the caravan's carrying capacity and return time
- **AND** purchased food is not counted as usable home supply before it can arrive

#### Scenario: Stock appears surplus only in the immediate period

- **WHEN** a sale would remove food needed for expected consumption or justified reserves
- **THEN** those feeding consequences are weighed against what the sale obtains before choosing whether to sell
- **AND** the governor may sell into reserves when the acquired goods matter more than the reserve's expected feeding contribution

#### Scenario: A forecast shortfall is far enough out for a caravan

- **WHEN** the forecast shows a shortfall in any goods later than a trading expedition could return
- **THEN** the expedition is compared with nearer sources using its full costs, including participants' absence
- **AND** the governor may send it when that comparison favors it

#### Scenario: A shortage is already present

- **WHEN** the colony is already short of a good or the shortfall arrives before any expedition could return
- **THEN** an expedition is not a response to it
- **AND** nearer sources and, for food, the scarcity priorities apply instead

#### Scenario: Participants are chosen by absence cost and trade contribution

- **WHEN** an expedition needs people and animals
- **THEN** each candidate's absence is priced by what labor, defense and other capabilities actually lose from that pawn, not by headcount
- **AND** a pawn whose trading skill helps and whose absence costs little is preferred

#### Scenario: A deal is accepted and confirmed

- **WHEN** a trade is selected
- **THEN** the decision uses current prices, available stock and the actual offered terms, and an unaffordable deal is refused with a reason
- **AND** completion is confirmed from the deal result and changed inventory, and changed terms require fresh review

## RENAMED Requirements

- FROM: `### Requirement: RG-ACQUIRE-002 — Treat food trade as an optional fully costed source`
- TO: `### Requirement: RG-ACQUIRE-002 — Treat trade as an optional fully costed source`
