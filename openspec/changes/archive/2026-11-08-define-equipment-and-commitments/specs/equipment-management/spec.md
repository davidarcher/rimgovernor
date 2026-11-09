## Purpose

Put the right weapons, armor, clothing and belts on the right people, keep
that gear serviceable, and change it when the fight plan asks.

This specification formalizes the maintainer's review on 2026-11-08, as traced
in the [decision record](../../changes/archive/2026-11-08-define-equipment-and-commitments/proposal.md#decision-provenance-and-review-boundary).
Hunting, wellbeing, protection and combat express equipment demand; this
specification owns how it is met. Resource acquisition compares the sources.
The scenarios are illustrative acceptance examples, not separately approved
tactics or evidence of passing behavior. The [wellbeing](../../../docs/requirements/colonist-wellbeing.md)
and [protection](../../../docs/requirements/protection-and-recovery.md) evidence
pages own the related inspection, execution and proof limits; no
implementation-completeness claim follows from this specification.

## ADDED Requirements

### Requirement: RG-EQUIP-001 — Assign gear by the contribution it changes

The governor SHALL choose each pawn's gear by the change it makes to what that
pawn contributes: for a fighter the harm dealt and taken against the forecast
threat, for a worker speed, warmth and mood, for a hunter the hunt. Gear SHALL
move between pawns when that raises the colony's total. Score formulas, role
precedence and fixed thresholds are candidates, not rules. Game facts bind;
player gear preferences do not under Auto.

#### Scenario: Heavy armor at a door and in the open

- **WHEN** a soldier could wear plate armor
- **THEN** its protection is weighed against its movement cost for the fight plan the colony expects, holding a door or moving in the open
- **AND** no fixed movement floor excludes it

#### Scenario: A better weapon arrives

- **WHEN** a weapon is looted, bought or crafted that beats a pawn's current one
- **THEN** the chain of swaps it enables across pawns is compared as one change
- **AND** a displaced weapon goes to whoever it now serves best

#### Scenario: A pawn's facts constrain the choice

- **WHEN** a pawn is a nudist, holds a title with required apparel, follows a precept with a dress rule, or owns a biocoded weapon
- **THEN** those facts bind the choice and are priced where they cost something
- **AND** a player-set apparel preference or manual assignment does not

#### Scenario: Arming a slave

- **WHEN** a slave could carry a weapon the plan needs
- **THEN** the option is compared with its rebellion consequence counted
- **AND** a prisoner is never armed (RG-COMBAT-002)

### Requirement: RG-EQUIP-002 — Keep gear serviceable through shared acquisition

The governor SHALL express demand for new and replacement gear priced by what
the gear changes, and SHALL let resource acquisition compare crafting,
purchase, loot and stripping as sources. Worn and tainted gear SHALL be
replaced when the replacement's contribution beats its cost; replacement is
never held as unsupported. No tier ladder or fixed recipe choice stands in for
that comparison.

#### Scenario: Armor is wearing out

- **WHEN** a fighter's armor is forecast to fail before the next expected threat
- **THEN** the replacement is demanded with the harm it prevents as its value
- **AND** acquisition compares making it, buying it and taking it from a defeated raider

#### Scenario: Tainted apparel from a raider

- **WHEN** a dead raider's apparel would fill a gap
- **THEN** wearing it is compared with its mood consequence for the wearer against going without or making new
- **AND** the mood consequence is priced by wellbeing, not a fixed exclusion

#### Scenario: A hunter needs a weapon

- **WHEN** a hunt the comparison favors has no armed hunter
- **THEN** the weapon is demanded as part of that hunt's cost through acquisition
- **AND** the hunt is compared with the weapon's time and cost included, not held

### Requirement: RG-EQUIP-003 — Change loadouts with the fight plan

When a fight plan is chosen (RG-COMBAT-001), the governor SHALL change gear as
part of that plan, priced within it: ranged weapons for a threat that is best
kept at range, EMP against mechanoids and shields, blunt weapons to down rather
than kill, belts for those who close. No threat class SHALL select a fixed
loadout, and a change that costs more fight time than it saves SHALL not be
made.

#### Scenario: Mechanoids arrive

- **WHEN** the standing hostiles include mechanoids
- **THEN** giving a shooter an EMP weapon is compared within the plan by the harm it prevents against the shots it costs
- **AND** the plan, not the threat label, decides who carries it

#### Scenario: Re-equipping would take too long

- **WHEN** the better weapon lies across the map and the enemy is already close
- **THEN** the swap is not ordered because its time costs more than it saves
- **AND** the plan proceeds with what the fighters hold
