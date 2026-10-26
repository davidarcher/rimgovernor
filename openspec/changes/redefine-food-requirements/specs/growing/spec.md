## Purpose

Provide crop outputs for feeding and other colony goals by selecting production
that fits available land, labor, growing conditions and the time goods are needed.

## ADDED Requirements

### Requirement: RG-GROW-001 — Plan crops against shared colony demand

The governor SHALL choose crop types, locations, area and planting times according
to colony goals, seasonal conditions and expected time to usable output. Food,
animal feed and non-food crops SHALL compete through shared acquisition for land,
labor and infrastructure. Harvest, delivery, processing and storage costs SHALL
contribute to the comparison.

#### Scenario: Food and clothing crops compete for growing capacity

- **WHEN** both feeding and material needs compete for available growing capacity
- **THEN** the governor chooses the crop allocation using their contributions and delivery times
- **AND** non-food crops are neither ignored nor assigned a separate unlimited production budget

#### Scenario: A crop cannot arrive before the feeding gap

- **WHEN** expected harvest arrives after food is needed
- **THEN** the crop is not credited as resolving that earlier gap
- **AND** food planning retains demand for a source that can meet it in time

#### Scenario: Production conditions change

- **WHEN** growing conditions, field capacity, crop survival or demand changes
- **THEN** the governor revises the crop plan and expected supply
- **AND** planned planting remains distinct from observed harvested stock

### Requirement: RG-GROW-002 — Invest proactively in useful protected production

The governor SHALL consider protected growing capacity before disruption when its
expected production and resilience benefits justify construction, operation and
competing resource costs. It SHALL include supporting inputs and dependencies
when comparing exposed, protected and mixed production strategies.

#### Scenario: Exposure threatens future supply

- **WHEN** expected seasonal or environmental exposure threatens useful crop production
- **THEN** the governor compares investment in protected capacity with reserves and other supply options
- **AND** can select the investment before exposed production fails

#### Scenario: Protected capacity depends on additional resources

- **WHEN** protected production requires supporting power, temperature control or other inputs
- **THEN** those costs, availability and failure dependencies are included in its expected benefit
- **AND** an unfinished structure alone does not establish productive capacity
