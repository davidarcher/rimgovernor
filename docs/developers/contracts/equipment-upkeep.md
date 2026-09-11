# Equipment and apparel upkeep

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`MaintainEquipment` is a maintained development goal in the shared ColonyPlan.
Emergencies suspend it. Hands issues its actions; neither the native read nor an
adviser independently starts work. Missing observations remain unknown.

## Native observations and selection

Planning `home/colony_facts` includes `gearUpkeep`. The separate
`home/gear_upkeep` preview reports current-map pawn identities, outfit/loadout
signatures, worn and primary item identities, condition, quality, armor and thermal
stats, comfortable temperature bounds, candidates and replacement needs.

Apparel candidates pass current outfit filters, developmental stage, body-part,
biocoding, reservation, safe reachability and resource-budget checks. Native
apparel scoring includes condition, armor, seasonal warmth and pawn-specific
requirements. A gain below the native 0.05 threshold does not trigger dressing.
Forced and locked apparel cannot be displaced. Orders do not change outfit filters
or create forced apparel entries.

Weapon upkeep preserves existing player assignments. It can arm an available
capable unarmed pawn and replace an upkeep-owned weapon at or below 50% condition
with the same native definition, at least 80% condition and no lower quality.
The native save retains the exact upkeep-owned weapon identity. A different
equipped weapon is a player assignment. Initial selection ranks eligible weapons
by pawn melee/shooting skill, native quality and condition with stable identity
tie breaking; it does not claim optimal combat damage across weapon definitions.

## Production and resource protection

Available eligible replacements precede production. A missing replacement can
select a discovered recipe on an existing workshop and add one `RepeatCount` bill.
Existing active production is preserved. Ingredient alternatives use native costs,
retain the inspected stuff where applicable, and respect player reserves, stopped
spending and shared plan commitments. Native ingredient admission and consumption
retain the existing production-policy guards. Required work types feed the shared
work-allocation method.

Production needs include damaged replaceable apparel, worn upkeep-owned weapons
and bounded native definition/stuff candidates that improve an observed thermal
deficit. A definition-level thermal estimate is a procurement candidate, not proof
of the eventual garment's quality, eligibility or sufficient protection.
Missing research, workshops, materials or suitable definitions remain explicit
blockers. This goal does not invent a trade or override a player outfit to obtain
an item. Workshop construction and economic decisions retain their own goals.

## Dispatch, completion and interruption

The native gear operation requires a paused game, exact pawn and target identities,
and a signature covering load, map, outfit/filter, primary weapon, worn identities
and forced/locked state. It rechecks eligibility at dispatch and starts ordinary
`Wear` or `Equip` work. Drafted, incapacitated, quest and player-ordered pawns are
preserved. Production also rechecks the retained pawn/loadout prerequisite and
available replacements before adding its bill.

The `pawn_gear` postcondition requires a later native observation of the exact
item in the pawn's apparel or primary equipment. Delivery of an order cannot
complete it. Lost receipts retain their pre-write timestamp and context; fresh
matching loadout evidence can reconcile success without replay. Changed load or
player direction blocks completion. Interrupted jobs and missing observations
cannot certify success. The shared watchdog bounds lack of progress.

Ordinary `pawn_equipped` weapon orders use the same passive completion recovery.
The pre-write record retains observation time, load and player direction even
when the native reply is lost. A later exact weapon observation can complete a
blocked order without sending it again. Changed context, cancelled work, unknown
pawn health and a different equipped item cannot clear the hold.

A bill receipt only confirms configuration. The maintained deficit remains until
usable gear is observed. A completed bill that produces an unsuitable item does
not trigger unlimited replacement bills under the same loadout prerequisite.

## Acceptance

Use the [equipment acceptance guide](../testing/equipment-upkeep.md) for the Docker
scenario. Fixture checks, native scripted pawn outcomes and sustained seasonal
campaigns are different evidence levels.
