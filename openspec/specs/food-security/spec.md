# Food security Specification

## Purpose

Keep colonists fed by acquiring, preparing, replacing and preserving usable food
under automatic control. Completion follows observed game outcomes, with expected
supply kept distinct from food produced and food eaten.

## Intent and evidence

These five requirements carry forward **documented intent**, not a new claim of
human approval or implementation completeness. Full autonomy was explicitly
confirmed on 2026-10-24; detailed acceptance scenarios remain **proposals for
review**. SHALL expresses the recorded obligation without upgrading its provenance.
Scenario formatting does not approve an example or establish a passing test.

[Food evidence and candidates](../../../docs/requirements/food-security.md)
owns implementation status, F1–F14, source provenance and execution limits.
RG-FOOD-005, RG-FOOD-007 and RG-FOOD-008 remain inferred candidates on that page,
outside this standing specification. Their IDs remain reserved to those entries.
The [collection](../../../docs/requirements/README.md) defines evidence terms;
the [review queue](../../../docs/requirements/review-queue.md) retains unresolved
source conflicts, proposed checks and decisions.

## Requirement provenance

All rows retain documented intent and proposed scenarios. Each evidence entry
records the original intent sources and the implementation and proof limits.

| Requirement | Intent sources and implementation evidence |
| --- | --- |
| RG-FOOD-001 | [Autonomous feeding](../../../docs/requirements/food-security.md#rg-food-001--feed-colonists-autonomously) |
| RG-FOOD-002 | [Replacement supply](../../../docs/requirements/food-security.md#rg-food-002--replace-food-before-the-colony-runs-out) |
| RG-FOOD-003 | [Cooking](../../../docs/requirements/food-security.md#rg-food-003--turn-available-ingredients-into-useful-food) |
| RG-FOOD-004 | [Continuation](../../../docs/requirements/food-security.md#rg-food-004--reassess-interrupted-food-work) |
| RG-FOOD-006 | [Preservation](../../../docs/requirements/food-security.md#rg-food-006--preserve-food-that-the-colony-will-need) |

## Requirements

### Requirement: RG-FOOD-001 — Feed colonists autonomously

While Auto and the food/work features are enabled, the governor SHALL notice
hunger and arrange food acquisition, preparation and eating as needed. Feeding
completion SHALL require observed consumption. This requirement covers mobile
colonists, ordinary edible food and usable food sources; special feeding remains
in candidate RG-FOOD-008.

#### Scenario: Obtain wild food without an armed hunter

- **GIVEN** an unarmed hungry mobile colonist, no food and reachable ripe berry bushes
- **WHEN** the governor runs under automatic control
- **THEN** it arranges berry acquisition and eating autonomously
- **AND** feeding remains incomplete until consumption is observed

#### Scenario: Eat while replacing supplies

- **GIVEN** a hungry colonist with suitable food available
- **WHEN** the governor assesses eating and future supply
- **THEN** it arranges eating while also arranging replacement supplies as needed

#### Scenario: No usable source

- **GIVEN** every available food source is unusable
- **WHEN** the governor assesses the food need
- **THEN** it reports the obstacle and retains the food shortfall

### Requirement: RG-FOOD-002 — Replace food before the colony runs out

When food stocks, consumers and relevant production conditions can be read, the
governor SHALL compare food on hand with expected consumption and arrange
replacement early enough to be useful. Planning SHALL account for delivery time,
labor and spoilage and keep expected supply separate from observed supply.
Reserve quantities follow the owning policy and contracts; existing constants
are not new product guarantees.

#### Scenario: Crops arrive after existing stock runs out

- **GIVEN** stored food will run out before a crop matures and a usable earlier source exists
- **WHEN** the governor compares replacement options
- **THEN** it arranges a source that can deliver sooner

#### Scenario: Forage from an unarmed start

- **GIVEN** an unarmed start with usable wild plants
- **WHEN** replacement food is needed
- **THEN** the governor can arrange forage

#### Scenario: Supply conditions change

- **GIVEN** food stocks fall or production is disrupted
- **WHEN** the governor observes the change
- **THEN** it reassesses replacement supply

#### Scenario: Expected harvest is not observed food

- **GIVEN** harvest work is designated but its output has not been observed
- **WHEN** the governor accounts for food supply
- **THEN** the designation remains expected supply rather than observed output

#### Scenario: Adequacy cannot be established from missing information

- **GIVEN** information required to establish food adequacy cannot be read
- **WHEN** the governor assesses supply
- **THEN** adequacy remains unresolved
- **AND** missing information is not credited as sufficient supply

### Requirement: RG-FOOD-003 — Turn available ingredients into useful food

When cooking is a suitable way to feed the colony, the governor SHALL arrange
preparation and cooking through to food produced in the game. A supported recipe,
usable equipment, eligible cook and suitable ingredients must be available or
capable of being provided. The governor chooses those details and may choose raw
food when that better meets the need. Cooking output and consumption are separate
outcomes.

#### Scenario: Produce food with a usable stove

- **GIVEN** a usable stove, suitable ingredients and an eligible cook for a supported recipe
- **WHEN** cooking is selected and performed
- **THEN** the chosen quantities are consumed and the expected food is produced

#### Scenario: Cooking inputs become unusable

- **GIVEN** an ingredient is lost or the selected stove becomes unusable
- **WHEN** the cooking attempt fails
- **THEN** the governor reports failure and reassesses the remaining inputs

#### Scenario: Restore lost cooking capacity

- **GIVEN** the colony loses its kitchen and still needs useful cooking capacity
- **WHEN** the governor reassesses food preparation
- **THEN** the cooking-capacity need reopens
- **AND** recovery remains incomplete until useful cooking becomes available again

### Requirement: RG-FOOD-004 — Reassess interrupted food work

While automatic control remains active, when food work fails or conditions change,
the governor SHALL read the actual outcome, reassess the remaining need and
continue with a suitable plan using fresh observations. Progress SHALL distinguish
work in progress, completed output and food eaten. This requirement concerns
continuation within a running session; save/reload is owned by continuity.

#### Scenario: Continue from observed output

- **GIVEN** harvesting or cooking has produced output
- **WHEN** the governor chooses the next food-work step
- **THEN** it uses the output observed in the game

#### Scenario: Cooking fails

- **GIVEN** a cooking attempt fails
- **WHEN** further food work is considered
- **THEN** the failed attempt ends and further work uses a fresh assessment of remaining ingredients

#### Scenario: Hunger has been satisfied

- **GIVEN** a colonist's hunger has been satisfied
- **WHEN** the governor reassesses food work
- **THEN** it reconsiders that colonist's future meal work

#### Scenario: Continuation lacks a required observation

- **GIVEN** an observation needed for food continuation is missing
- **WHEN** the governor assesses continuation
- **THEN** it exposes the limitation and leaves the food shortfall unresolved

### Requirement: RG-FOOD-006 — Preserve food that the colony will need

The governor SHALL arrange suitable storage and cooling for food at risk while
keeping it accessible when needed. Observed storage and temperature SHALL
determine whether preservation needs remain open. Cooling demand follows the
owning policy threshold; a planned cooler alone does not establish preservation.

#### Scenario: Warm perishable stock

- **GIVEN** warm perishable stock above the policy threshold
- **WHEN** the governor assesses preservation
- **THEN** it creates cooling work

#### Scenario: Food is exposed

- **GIVEN** food is left exposed
- **WHEN** the governor assesses storage
- **THEN** the exposed food creates storage demand

#### Scenario: Preservation is disrupted

- **GIVEN** power fails or a storage route becomes unusable
- **WHEN** the governor reassesses preservation
- **THEN** it retains the threatened supply in its assessment and seeks useful work

#### Scenario: Cooler is only planned

- **GIVEN** a cooler is planned but the needed temperature outcome has not been observed
- **WHEN** the governor assesses preservation progress
- **THEN** the temperature outcome remains pending
