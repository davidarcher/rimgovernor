# Food security Specification

## Purpose

Keep everyone dependent on the colony adequately fed by preventing starvation
and its harmful effects. Manage preparation, useful intake, reserves, production,
preservation and feeding access as part of the colony's overall goals and risks.

## Authority and evidence

The maintainer confirmed the policy in the food exploration on 2026-10-25 and
explicitly authorized replacing the recovered food baseline. The
[decision record](../../changes/redefine-food-requirements/proposal.md#decision-provenance-and-review-boundary)
traces those decisions. The requirement wording formalizes that direction;
scenarios are illustrative acceptance examples, not separately approved tactics
or evidence that the implementation passes them.

The [food evidence page](../../../docs/requirements/food-security.md) retains
historical source and test inspection with its original revision and proof
limits. Its previous support labels apply to the narrower inspected paths,
not certification of these expanded requirements. No new gameplay result or
implementation audit is established by replacing this specification.

RG-FOOD-001–008 retain their identities. The previously inferred growing, hunting
and assisted-feeding intent is now confirmed here; old test-driven restrictions
are not product policy. RG-FOOD-009–013 cover additional agreed decisions.
No fixed eating threshold, reserve duration, crop list or channel count is imposed.

## Related responsibilities

Food management owns feeding outcomes and demand. Equipment management provides
hunting capability; habitat provides useful kitchens, storage and dining;
travel owns journeys. Shared player explanations expose important feeding
conditions and decisions. These dependencies must serve the complete feeding path.

The same decision record captures related capability drafts:
[resource acquisition](../../changes/redefine-food-requirements/specs/resource-acquisition/spec.md),
[hunting](../../changes/redefine-food-requirements/specs/hunting/spec.md),
[growing](../../changes/redefine-food-requirements/specs/growing/spec.md) and
[herd management](../../changes/redefine-food-requirements/specs/herd-management/spec.md).
They describe combined yields, shared costs, production and herd choices; they
are not additional standing capability migrations or new implementation evidence.

## Requirements

### Requirement: RG-FOOD-001 — Feed all colony dependents

While automatic control is active, the governor SHALL manage feeding to prevent
starvation and its harmful effects for all colony dependents, including colonists,
guests, prisoners and animals. It SHALL arrange the acquisition, preparation and
access needed for feeding, retaining unmet needs when no adequate option exists.
Feeding outcomes SHALL be established by observed consumption and condition.

#### Scenario: Aggregate stock conceals an unfed dependent

- **WHEN** the colony has food but a dependent cannot obtain suitable nutrition
- **THEN** that dependent's feeding need remains unmet
- **AND** the governor arranges suitable feeding rather than declaring adequacy from total stock

#### Scenario: Native behavior already satisfies a feeding need

- **WHEN** native pawn behavior obtains and consumes suitable food without a governor-issued eating action
- **THEN** the governor accounts for the actual feeding outcome
- **AND** assesses future supply and other dependents without requiring redundant intervention

### Requirement: RG-FOOD-002 — Replace food before the colony runs out

The governor SHALL maintain reserves and use multiple worthwhile food channels
to reduce starvation risk, accounting for plausible disruptions and recovery
time. It SHALL autonomously balance resilience against competing colony needs,
considering seasonal supply, projected threats, losses and shared dependencies
across acquisition, preparation, storage and feeding. Changing dependent numbers
and controlled commitments SHALL enter that assessment.

#### Scenario: Several channels share a vulnerability

- **WHEN** a plausible disruption could impair multiple sources or their common kitchen, power or delivery capacity
- **THEN** the governor accounts for the combined exposure when choosing reserves and alternative capacity
- **AND** nominal source count alone does not establish resilience

#### Scenario: Additional preparation competes with other colony needs

- **WHEN** greater reserves or another food channel would consume resources useful elsewhere
- **THEN** the governor weighs the expected resilience benefit against those competing uses
- **AND** neither a fixed reserve duration nor maximum possible stock defines the required choice

#### Scenario: Expected dependents increase

- **WHEN** herd growth, arrivals or a controlled commitment would increase feeding demand
- **THEN** their expected consumption and feeding requirements enter the assessment before the decision or arrival when foreseeable
- **AND** unplanned arrivals trigger reassessment of supply and reserves

### Requirement: RG-FOOD-003 — Turn available ingredients into useful food

The governor SHALL choose preparation methods by their net feeding contribution,
including nutrition gained and actually used, mood, health, labor, ingredients,
infrastructure and spoilage. It SHALL provide and maintain justified preparation
capacity. Better meals SHALL be considered when their benefits justify their
costs; cooked food is normally expected to win, while raw food remains an option
whose lost conversion yield and other consequences must be evaluated.

#### Scenario: Preparation increases usable nutrition

- **WHEN** preparing ingredients can yield more useful feeding than eating them directly
- **THEN** the comparison credits that additional nutrition and includes preparation costs and readiness
- **AND** preparation is not valued solely as a mood improvement

#### Scenario: Better meals offer a mood benefit

- **WHEN** suitable ingredients permit meals with greater mood benefit
- **THEN** the governor weighs that benefit against actual ingredient, labor and other costs
- **AND** does not assume every higher-quality recipe consumes more ingredient nutrition

#### Scenario: Preparation is unavailable before feeding is needed

- **WHEN** prepared food cannot be supplied in time but suitable raw food is available
- **THEN** the governor compares immediate raw feeding with the consequences of waiting
- **AND** includes the nutrition conversion forgone by consuming ingredients directly

#### Scenario: Preparation conditions create avoidable illness risk

- **WHEN** preparation conditions or available methods differ in food-safety risk
- **THEN** the governor compares expected illness consequences with the costs of safer methods or improved facilities
- **AND** food safety contributes to preparation investment and method choice

### Requirement: RG-FOOD-004 — Reassess interrupted food work

The governor SHALL reassess feeding and supply when work fails or conditions
change, using actual outcomes. It SHALL distinguish expected acquisition,
delivered ingredients, prepared output and consumed nutrition. Missing required
observations SHALL leave the affected claim unresolved. It SHALL expose material
shortages, blockers, emergency exceptions and major food investments through
the shared player explanation capability.

#### Scenario: A kitchen or supply source is lost

- **WHEN** preparation capacity or a selected food source becomes unavailable
- **THEN** the governor revises effective supply and remaining work using current conditions
- **AND** chooses useful recovery or alternatives instead of replaying the previous plan unchanged

#### Scenario: Output is expected but not observed

- **WHEN** harvesting, hunting or cooking is ordered but output has not been observed
- **THEN** the expected output remains separate from available food
- **AND** an order receipt does not establish completed feeding

#### Scenario: Feeding cannot be established

- **WHEN** a required observation is missing or all usable sources are blocked
- **THEN** the governor retains the unresolved feeding need and exposes the limitation
- **AND** missing facts are not interpreted as adequate supply

### Requirement: RG-FOOD-005 — Grow food for the coming shortage

The governor SHALL derive food-crop and animal-feed demand from expected
consumption, reserves, seasonal gaps and time to usable harvest. It SHALL compare
growing with other food channels through shared acquisition, with crop production
competing against non-food crops and other colony work. Protected growing SHALL
be considered proactively when its production and resilience benefits justify
its full costs.

#### Scenario: Harvest arrives too late

- **WHEN** expected crop output cannot arrive before a feeding shortfall
- **THEN** growing does not close the earlier shortfall
- **AND** the governor seeks a timely complementary source while retaining useful longer-term production

#### Scenario: Protected growing can reduce foreseeable exposure

- **WHEN** exposed production leaves useful supply vulnerable to seasonal or environmental conditions
- **THEN** the governor compares protected production with reserves and other channels before a loss occurs
- **AND** includes supporting inputs, construction and operating costs

### Requirement: RG-FOOD-006 — Preserve food that the colony will need

The governor SHALL develop and maintain preservation capacity for ongoing supply
and reserves, valuing repeated future production and consumption as well as
current stock. It SHALL consider freezing as a normal investment when feasible
and worthwhile, include its supporting costs and failure exposure, and choose
reserve foods and storage with useful remaining shelf life after disruption.
Durable reserves may also be frozen.

#### Scenario: A freezer protects repeated future harvests

- **WHEN** freezing capacity can protect useful current and future supply
- **THEN** its comparison includes the ongoing preservation benefit and supporting infrastructure
- **AND** a small present stock alone does not make the investment valueless

#### Scenario: Refrigeration is not yet feasible

- **WHEN** the colony needs reserves before dependable refrigeration is available
- **THEN** the governor considers suitable available durable foods and preservation methods
- **AND** accounts for their ingredients, preparation effort, eligibility and remaining shelf life

#### Scenario: Frozen durable reserves lose cooling

- **WHEN** cooling fails for durable reserve food such as pemmican
- **THEN** the governor assesses how long that food remains usable in the changed conditions
- **AND** values freezing and unfrozen shelf life as complementary protections

#### Scenario: Prepared batches would spoil before use

- **WHEN** harvesting, preparation and consumption timing would create avoidable food losses
- **THEN** the governor compares batch sizes, timing, preservation and use of existing stocks to reduce those losses
- **AND** counts food that will spoil before use as unavailable for that later feeding

### Requirement: RG-FOOD-007 — Account for the cost and danger of hunting

The governor SHALL assess hunting's contribution to food supply using expected
usable nutrition, delivery time, labor, capability requirements and danger,
together with useful non-food outputs. It SHALL include retrieval, processing
and preservation needed before feeding. Hunting capability and equipment
acquisition SHALL be evaluated as dependencies rather than presumed available.

#### Scenario: Hunting can serve more than feeding

- **WHEN** a hunt could supply useful meat and leather, or useful leather while meat is plentiful
- **THEN** shared acquisition compares its combined contribution and charges shared costs once
- **AND** food planning values only the edible output useful to its dependents

#### Scenario: A potential food source needs an armed hunting party

- **WHEN** a useful hunting option needs capabilities or equipment not currently ready
- **THEN** its comparison includes obtaining those capabilities and the resulting delay
- **AND** a kill alone is not credited as delivered or consumed food

### Requirement: RG-FOOD-008 — Feed people who need suitable food or help

The governor SHALL assess food suitability and access for each dependent,
including animals and people needing assistance. It SHALL arrange suitable food,
delivery and assisted feeding as needed, accounting for caregiver availability.
Available stock SHALL not establish feeding adequacy for a dependent unable to
reach, consume or receive it.

#### Scenario: A patient or baby cannot feed independently

- **WHEN** a dependent requires another pawn to provide suitable food
- **THEN** the governor accounts for a caregiver and the delivery and feeding work
- **AND** provisioning ingredients alone does not establish completed feeding

#### Scenario: Food exists outside the dependent's usable access

- **WHEN** a prisoner, penned animal or other dependent cannot access suitable available food
- **THEN** the governor arranges usable feeding access or delivery
- **AND** the inaccessible stock does not close that dependent's shortfall

### Requirement: RG-FOOD-009 — Choose eating times and portions for useful intake

The governor SHALL weigh nutrition actually retained against hunger's mood and
other consequences when choosing eating times and portions. It SHALL account for
the eater's capacity, food portions and delays in reaching and consuming food.
Neither the native eating threshold nor minimizing all nutrition waste SHALL
dictate the choice independently of those consequences.

#### Scenario: Waiting can reduce overconsumption waste

- **WHEN** delaying a meal would allow more of its nutrition to be retained
- **THEN** the governor compares that saving with hunger consequences and feeding-delay risk
- **AND** can choose either earlier feeding or justified delay according to the situation

#### Scenario: Nominal nutrition exceeds useful intake

- **WHEN** a serving exceeds the eater's available capacity at consumption
- **THEN** food planning credits the useful intake rather than treating excess nutrition as retained

### Requirement: RG-FOOD-010 — Respect restrictions and weigh emergency exceptions

The governor SHALL respect dietary and ethical restrictions normally. In a food
emergency it SHALL consider exceptions across all valid game actions, including
cannibalism, by weighing harm avoided against acquisition, consumption, mood and
other consequences. An exception SHALL not make biologically unusable food usable
or establish a permanent change to normal restrictions.

#### Scenario: Compliant food can meet the need in time

- **WHEN** suitable compliant feeding can prevent the threatened harm in time
- **THEN** the governor respects normal dietary and ethical restrictions

#### Scenario: Restricted actions could prevent food harm

- **WHEN** compliant options cannot prevent starvation or its harmful effects in time
- **THEN** the governor evaluates otherwise restricted acquisition and consumption, including cannibalism when valid
- **AND** compares their full consequences rather than automatically selecting an exception
- **AND** returns to compliant feeding when viable

### Requirement: RG-FOOD-011 — Prioritize dependents under unavoidable scarcity

When feeding resources cannot cover all dependents, the governor SHALL prioritize
colonists, then guests, then prisoners and animals, weighing consequences within
those priorities. It SHALL consider release or other justified changes for
dependents it cannot sustain. Releasing prisoners or animals for lack of feed
SHALL be an exceptional response rather than the routine feeding strategy.

#### Scenario: Insufficient suitable food can serve different groups

- **WHEN** scarce usable feeding resources cannot cover colonists, guests, prisoners and animals
- **THEN** allocation prioritizes colonists over guests and guests over prisoners and animals
- **AND** choices between prisoners and animals depend on consequences without a fixed ordering between them

#### Scenario: Retained dependents cannot be sustained

- **WHEN** the colony cannot sustain prisoners or animals with available feeding options
- **THEN** the governor compares release and other valid responses according to their consequences
- **AND** normal planning seeks to avoid recurring emergency releases through timely preparation

### Requirement: RG-FOOD-012 — Feed dependents during world travel

The governor SHALL include feeding in quests, caravans and other away activity,
accounting for suitability, spoilage, assistance, journey duration and plausible
delays. It SHALL retain adequate feeding provision at home when allocating travel
supplies and participants, and reassess changes. Inaccessible home stock SHALL
not establish food adequacy for travelers.

#### Scenario: A mission takes feeding resources away from home

- **WHEN** an away activity would consume food or remove feeding participants from the colony
- **THEN** planning includes suitable provisions and feeding access for travelers and remaining dependents
- **AND** compares those costs and risks with the mission's benefit before commitment

#### Scenario: Travel lasts longer than expected

- **WHEN** a route, delay, participant or supply change alters feeding prospects
- **THEN** the governor reassesses usable provisions and the mission plan
- **AND** food that cannot reach the travelers is not treated as available to them

### Requirement: RG-FOOD-013 — Provide worthwhile dining conditions

The governor SHALL provide and use suitable dining arrangements when their mood
and other benefits justify space, construction, travel and upkeep costs. It SHALL
account for access, individual needs, urgency and total colony cost. Habitat
planning SHALL provide justified facilities; ownership of a table alone SHALL
not establish adequate dining provision or delay necessary feeding.

#### Scenario: A table exists without usable dining capacity

- **WHEN** seating, occupancy, access or distance prevents practical use of a table
- **THEN** the governor assesses actual usable dining provision for the affected dependents
- **AND** compares justified additional capacity or access changes with other colony needs

#### Scenario: Urgency or individual needs favor another feeding arrangement

- **WHEN** danger, urgent hunger, patient care or away work makes ordinary dining impractical
- **THEN** the governor weighs dining benefits against the consequences of reaching or waiting for facilities
- **AND** arranges appropriate feeding without requiring normal dining conditions first
