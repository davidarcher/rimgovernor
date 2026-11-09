# Proposal

## Why

Four standing specifications route demand to "equipment management" (hunting
for weapons, wellbeing for clothing, protection for readiness, combat for
loadouts) and nothing owned it, while the brain carries a weapon score formula,
an apparel role precedence, gap and quality thresholds, a speed floor and a
weapon tier ladder with no requirement over them. Colony commitments was the
last page never maintainer-confirmed: RG-COMMIT-001 recovered from contracts
and RG-COMMIT-003 still an inferred candidate, with five held native
operations. The maintainer reviewed both on 2026-11-08. This change records the
decisions and promotes both capabilities to standing specifications.

## What Changes

- Add the `equipment-management` capability with RG-EQUIP-001–003: gear
  assigned by the contribution it changes, kept serviceable through shared
  acquisition, and changed with the fight plan.
- Drop the player-set apparel preference and player weapon assignments as
  inputs under Auto; game facts (traits, titles, precepts, biocoding) stay.
  Gear swaps between pawns are supported.
- The apparel replacement hold (`unsupported_forecast_apparel_replacement`)
  is a defect to remove; replacement is demand like any other.
- Arming a slave is a priced option with its rebellion consequence counted.
  Prisoners stay unarmed (RG-COMBAT-002).
- Add the `colony-commitments` capability with RG-COMMIT-001 and
  RG-COMMIT-003 confirmed, identities preserved. Taking people on is priced
  by contribution against obligations with no population target. Prisoner
  outcomes are priced purely by consequence, the colony's precepts included
  through the consequences the game attaches.
- Lift the five held native operations (rituals, bestowing, neuroformers,
  monolith interaction, population capture and lance use) into the priced
  comparison. RG-COMMIT-003 is promoted from inferred candidate to confirmed
  intent.

## Capabilities

### New Capabilities

- `equipment-management`: RG-EQUIP-001–003.
- `colony-commitments`: RG-COMMIT-001, RG-COMMIT-003 (RG-COMMIT-002 lives in
  herd management).

## Code findings for the implementing change

Reported from a read of the current code on 2026-11-08; indicative.

- `WeaponAssignmentRules.ScoreWeapon` scores damage times an accuracy term of
  Shooting skill with role multipliers (1.3 ranged roles, 3 melee); apparel
  roles follow a fixed precedence with gap thresholds (soldier 0.05, hunter
  0.1, slave and prisoner 0.5, others 0.2), `GearArmorSpeedFloor` -0.5 keeps
  soldiers out of plate and cataphract armor, `ApparelMinHP` 0.51 and a 50%
  tattered cutoff decide "worn out", and `GearSearchBudget` caps the ensemble
  search. Under RG-EQUIP-001 these are initial candidates with provenance.
- `ArmoryWeaponDemand` picks a recipe on a tier ladder whose threat tier is
  Fabrication when the raid outsizes the defense and Neolithic otherwise; no
  make, buy, loot or strip comparison exists ("this concern does not invent a
  trade"). Under RG-EQUIP-002 the demand goes to shared acquisition.
- `MilitaryReview.EquipmentVerdict` holds replacement as
  `unsupported_forecast_apparel_replacement` (#4009); the contract says
  automatic physical replacement is unsupported. Lifted.
- `ApparelPreference` (Dressed, Nude, ArmorOnly) and never displacing a
  player weapon assignment are player inputs; dropped under Auto.
- `ClassifyLoadoutThreat` maps a fight to one of four loadouts (pods, tribal,
  EMP, prison break) by hostile kind; under RG-EQUIP-003 the loadout is part
  of the priced fight plan.
- `HuntPrerequisiteRules` reprices a hunt plus a 6000-tick weapon craft with a
  0.1-day lead; a candidate, not a rule.
- Commitments: the population contract's food reserve and capacity targets are
  implementation policy; `IdeologyFamily`, `JoinersFamily`, `PrisonersFamily`,
  `RoyaltyFamily` and `AnomalyFamily` hold rituals (#3580), bestowing (#4002),
  capture and lance use (#4003), neuroformers (#4004) and monolith interaction
  (#4005). All five are now implementation work under RG-COMMIT-003 and
  RG-COMBAT-004, not known gaps.

## Impact

Requirement documents and navigation only. No planner, operation or contract
changes. The commitments page keeps K1–K6 and cites the specification; the
wellbeing page's W7–W8 and the protection page's P5 are the inspected
equipment paths. No implementation-completeness or pass claim follows. The
issues named above stay open as implementation work.

## Decision provenance and review boundary

Source: the maintainer's review in this chat on 2026-11-08. Each row records
the maintainer's direction and the words that gave it; requirement wording and
scenarios are the assistant's formalization.

| Confirmed decision | Maintainer direction |
| --- | --- |
| Taking people on | Priced by expected contribution against obligations over the forecast horizon; no population target ("1 confirm"). |
| Prisoner outcomes | Pure pricing by consequence, no named exclusions; precepts enter through the consequences ("pure pricing by consequence. the precepts already are counted on the consequences."). |
| Held native operations | All five lifted into the comparison, the monolith included, priced by its consequences and research benefits ("lift all, there should be data on expected consequences/combat/etc for monolith with some research buffs i think?"). |
| RG-COMMIT-003 | Confirmed as written and promoted from candidate ("4 confirmed"). |
| Gear assignment | By the contribution it changes; the score formula, role precedence, thresholds and speed floor are initial candidates ("1 confirm"). |
| Replacement and sourcing | Through shared acquisition; the replacement hold is a defect; swapping gear between pawns is supported ("2 confirm. swapping between pawns should be supported."). |
| Player gear inputs | Dropped under Auto; game facts stay ("3 drop player inputs"). |
| Arming slaves | A priced option with the rebellion consequence ("4 price it"). |

No quality floor, wear threshold, armor speed limit, role precedence,
population ceiling, food reserve ratio or prisoner policy was approved. The
defense pricing gap accepted on 2026-11-03 still applies. RG-COMMIT-002 stays
in herd management; no identity is reused.
