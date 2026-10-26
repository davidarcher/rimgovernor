## Purpose

Obtain useful goods for colony goals through a common comparison of available
sources, their combined contributions, delivery times, costs and risks.

## ADDED Requirements

### Requirement: RG-ACQUIRE-001 — Compare combined contributions and shared costs

The governor SHALL compare acquisition options across colony goals, crediting all
useful outputs while charging shared work and resources once. Selection SHALL
account for existing stock, commitments, competing resources, time to usable
delivery and risk. Food demand SHALL participate in that shared comparison.

#### Scenario: A hunt serves leather demand when food is sufficient

- **WHEN** leather is needed and hunting is an available source although meat is plentiful
- **THEN** the hunt is eligible for comparison based on useful leather and any useful meat
- **AND** the hunt's shared costs are charged once rather than separately for each output

#### Scenario: Crops compete for a common production budget

- **WHEN** food, animal feed and clothing crops compete for land and labor
- **THEN** their contributions to colony goals are compared against the same available resources
- **AND** the same field capacity or worker time is not promised to multiple incompatible uses

#### Scenario: Acquisition has not delivered

- **WHEN** work is planned or issued but its useful output has not reached the required destination
- **THEN** expected supply remains distinct from available inventory
- **AND** outstanding commitments remain accounted for without double-counting their output

### Requirement: RG-ACQUIRE-002 — Treat food trade as an optional fully costed source

The governor SHALL consider food and ingredient trades when their expected
contribution justifies their full costs and risks. Purchases and surplus sales
SHALL account for future feeding and reserves. A dedicated trading expedition
SHALL include travel, provisions, carrying capacity, danger and absence from
colony work; routine food-buying expeditions are not required.

#### Scenario: An accessible trader offers useful food

- **WHEN** a trader offers food that could improve colony supply
- **THEN** the purchase is compared with other uses of the resources and other food sources
- **AND** the trader's existence alone does not require a purchase

#### Scenario: A distant purchase consumes resources before food returns

- **WHEN** an expedition could buy food away from home
- **THEN** the comparison includes the journey's provisions, opportunity costs, risks and delivery time
- **AND** purchased food is not counted as usable home supply before it can arrive

#### Scenario: Stock appears surplus only in the immediate period

- **WHEN** a sale would remove food needed for expected consumption or justified reserves
- **THEN** those feeding consequences are included before choosing whether to sell
