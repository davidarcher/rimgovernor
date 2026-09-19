# Equipment and apparel upkeep

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`MaintainEquipment` is a maintained development goal in the shared ColonyPlan.
Emergencies suspend it. Hands issues its actions; neither the native read nor an
adviser independently starts work. Missing observations remain unknown. The goal
ranks for an optional development slot like any other priority-3 need (labor
profile Tailoring, Smithing or Crafting) and its methods are admitted only while
it holds one.

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
or create forced apparel entries. A loadout carries at most 8 candidates, the
best by gain then thing id; the eligible items past that bound count as
`filtered` in the loadout's completeness, so the routine colony facts do not
grow with pawns x loose items (issue #320). MaintainEquipment only wears the
best funded candidate, and the wear order's own census applies the same bound.
The census candidates are apparel only; loose weapons are the equip family's
(issue #339). A `gear_replace` whose candidate the fresh census no longer offers
as apparel, or whose wear preview refuses `NOT_FOUND`, is cancelled rather than
held, so the plan closes and the goal's development slot frees at the next
review, as haul and supply do for a thing that left its cell.

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
Existing active production is preserved. Ingredient alternatives use native costs
and respect player reserves, stopped spending and shared plan commitments. The
inspected stuff is a preference: a worn-out cloth shirt is replaced from cloth
when cloth is funded and otherwise from any funded material the recipe accepts
(leather from hunting is the usual interim before a cotton field). Native
ingredient admission and consumption retain the existing production-policy
guards. Required work types feed the shared work-allocation method: while the
goal is in deficit, every standing bench recipe that produces a reported
replacement need contributes its work type to `EnsureWorkAssignments` before
any bill exists, the same way a resource deficit covers its benches, because
native bill admission refuses a bench nobody works.

Production needs include damaged replaceable apparel (`wear`), worn upkeep-owned
weapons, garments covering a core body-part group (Torso, Legs) the pawn wears
nothing over (`missing`, the warmest budgeted candidates), and bounded native
definition/stuff candidates that improve an observed thermal deficit (`cold`,
`heat`). An uncovered core group is a deficit in any weather. A definition-level
thermal estimate is a procurement candidate, not proof of the eventual garment's
quality, eligibility or sufficient protection.

The review's apparel-condition census (`GearReview.WornOut`, `Uncovered`) is the
fraction of colonists wearing any garment at or under the 50% tattered threshold
and the fraction with a core group uncovered, derived from the same loadout read;
it is known only when every colonist's worn apparel was observed and does not
decide recovery, which follows the native deficit flags.
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

On the operations contract the apparel order is `ImproveGear`
(`NativeGearOperations`): its pawn precondition carries the pawn's control
snapshot token, its target the candidate's supply token, and
`expected_loadout_token` the loadout signature, all as the colony gear census
(`NativeGearFacts`) emitted them. A matching preview projects a `Wear` job on
the exact pawn and apparel; execution issues that job as ordered (not forced)
work and reports it applied only when the job is current or the apparel is
already worn. Progress reads the same record: worn completes, the live job
pends, anything else is an interruption. Weapons stay on the `Equip`
pawn-target order.

The `pawn_gear` postcondition requires a later native observation of the exact
item in the pawn's apparel or primary equipment. Delivery of an order cannot
complete it. Lost receipts retain their pre-write timestamp and context; fresh
matching loadout evidence can reconcile success without replay. Changed load or
player direction blocks completion. Interrupted jobs and missing observations
cannot certify success. The shared watchdog bounds lack of progress.

Ordinary `pawn_equipped` weapon orders use the same passive completion recovery.
The pre-write record retains observation time, load and plan revision even
when the native reply is lost. A later exact weapon observation can complete a
blocked order without sending it again. Changed context, cancelled work, unknown
pawn health and a different equipped item cannot clear the hold.

A bill receipt only confirms configuration. The maintained deficit remains until
usable gear is observed. A completed bill that produces an unsuitable item does
not trigger unlimited replacement bills under the same loadout prerequisite.

## Acceptance

`acceptance run production/apparel` (issue #233): a colonist in a tattered cloth
shirt, a hand tailoring bench and only plain leather in stock; the service must
raise the shirt bill from the leather and dress the colonist in the product. Fixture
checks, native scripted pawn outcomes and sustained seasonal campaigns are
different evidence levels.
