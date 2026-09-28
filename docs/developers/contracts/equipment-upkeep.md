# Equipment and apparel upkeep

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md) · [Apparel policy operation](apparel-policy.md) · [Weapon planner](weapon-planner.md)

`MaintainEquipment` is a maintained development goal in the shared ColonyPlan.
Emergencies suspend it. Hands issues its actions; neither the native read nor an
adviser independently starts work. Missing observations remain unknown. The goal
ranks for an optional development slot like any other priority-3 need (labor
profile Construction, Tailoring, Smithing or Crafting) and its methods are admitted only while
it holds one. The [pawn profile](work-assignment.md#pawn-profile) exports the
per-pawn apparel and weapon flags (Nudist, Ascetic, Brawler's `MeleeOnly`) the
loadout planner consumes. The role apparel policies are the
[apparel-policy operation](apparel-policy.md); weapon assignment is the
[weapon planner](weapon-planner.md); this page is the loadout model.

## Native observations and selection

Planning `home/colony_facts` includes `gearUpkeep`. The separate typed
`Observations.ReadGear` read reports current-map pawn identities, outfit/loadout
signatures, worn and primary item identities, condition, quality, armor and thermal
stats, comfortable temperature bounds, candidates and replacement needs.

Apparel candidates pass current outfit filters, developmental stage, body-part,
biocoding, reservation, safe reachability and resource-budget checks. Native
apparel scoring includes condition, armor, seasonal warmth and pawn-specific
requirements. A gain below the native 0.05 threshold does not trigger dressing.
Individual wear orders obey the pawn's current apparel policy (the role
policy the [apparel-policy operation](apparel-policy.md) assigns) and do not
create forced entries. A loadout carries at most 8 candidates, the
best by gain then thing id; the eligible items past that bound count as
`filtered` in the loadout's completeness, so the routine colony facts do not
grow with pawns x loose items (issue #320). MaintainEquipment only wears the
best funded candidate, and the wear order's own census applies the same bound.
The census candidates are apparel only; loose weapons are the equip family's
(issue #339). A `gear_replace` whose candidate the fresh census no longer offers
as apparel, or whose wear preview refuses `NOT_FOUND`, is cancelled rather than
held, so the plan closes and the goal's development slot frees at the next
review, as haul and supply do for a thing that left its cell.

## Production and resource protection

Available eligible replacements precede production. A missing replacement can
select a discovered recipe and issue a finite bill for the colony gap count. When its workshop
is missing, the shared workshop ladder stages the bench in a suitable room (or
stages the room first) and raises research prerequisites under `EnsureResearch`.
Existing active production is preserved. Ingredient alternatives use native costs
and respect player reserves, stopped spending and shared plan commitments. The
inspected stuff is a preference: a worn-out cloth shirt is replaced from cloth
when cloth is funded and otherwise from any funded material the recipe accepts
(leather from hunting is the usual interim before a cotton field). Native
bills replace their ingredient membership with the funded material set, retained
through persistence and the applied intent. Native ingredient
admission and consumption keep stock committed to construction and other
pawns' bill jobs. Required work types feed the shared work-allocation method: while the
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
it is known only when every colonist's worn apparel was observed. With complete
loadout-model inputs, condition and coverage contribute to scored gaps; older
observations retain native-deficit recovery.
Missing research, workshops, materials or suitable definitions remain explicit
blockers. This goal does not invent a trade or bypass native apparel eligibility to obtain
an item. Bench staging uses the equipment goal through the shared workshop ladder.

## Loadout model

`policy.PlanGearLoadout` consumes a complete, eligible def × stuff × quality
catalog and worn gear. The native gear census supplies it per pawn
(`GearLoadout.loadout_model`, with the snapshot's finished research and
outdoor temperature): gender, traits, every worn garment, the eligible loose
or stored candidates and the producible definitions whose recipe research is
finished (one stuff each, the most stocked), at most 64 unworn options.
Go derives each option's slot from its apparel layers and body-part groups,
takes the role from the apparel-policy read and narrows unworn options to
the definitions that role's apparel policy permits. A census without the
model, or one the model's bounds refuse (for example two worn garments on
one slot), leaves `GearPawn.LoadoutModel` unknown and the native deficit,
replacement and single-item bill path active. This pure Go model neither discovers
products nor issues orders. Catalog providers resolve native material stats,
outfit/body/stage eligibility and available production resources before planning.
Stats are Normal-quality values for the specific material; armor multipliers are
0.6/0.8/1/1.15/1.3/1.45/1.8 and insulation multipliers
0.8/0.9/1/1.1/1.2/1.5/1.8, Awful through Legendary.

The optional seasonal observation carries 12 outdoor temperatures sampled at local
twelfth midpoints and indexed by native Twelfth, the current twelfth and ticks to
its next boundary, and at most one
ColdSnap/HeatWave row with its native temperature offset and remaining ticks
(-1 for permanent). Loadout scoring covers the current and next two twelfths
for both cold and heat, retaining current ambient extremes. Weather extends
the target only over twelfths it overlaps, including beyond the normal lookahead
when its duration is longer. Missing seasonal fields retain ambient-only scoring.
The complete product-catalog requirement still applies to modeled loadouts.

Targets cover skin torso, skin legs, middle torso, outer, belt, headgear and
primary weapon. An exact bounded ensemble search rejects shared layer AND body
group conflicts and preserves locked items. It scores armor, current ambient
thermal needs, movement, condition, cost, coverage and taint. Coverage requires
legs for men and legs plus chest for women; nudists prefer no body apparel.
Taint costs 5/8/11/14 points for one/two/three/four-or-more items, except for
Bloodlust and Inhuman. Replacements cannot assume a separate strip order.

Roles use existing work priorities, skills and trait facts plus explicit child,
slave, incapable-of-violence and drafted-squad status. Precedence is child,
slave, non-combatant, soldier, then highest-priority work (stable role-name tie
break). Hunters require ranged range at least 25 and favor warm outerwear;
workers reject movement penalties, indoor workers reduce thermal weight,
soldiers favor sharp then blunt armor with helmets gated by Smithing and
shields restricted to melee, children select Kid/Apparel_Kid definitions, and
slaves favor low cost. Garment stats and conflict metadata determine combinations
such as a flak vest beneath a duster; definitions are not hard-coded.

Armor ladder (#470). An option carries its recipe's research and ingredients;
it is eligible only once the input's finished-research census names every
project, so a soldier's gaps progress simple helmet (Smithing), then flak vest
and flak helmet, then flak jacket and pants (FlakArmor), and recon or marine
armor only once their plasteel and advanced components are funded. Soldiers
refuse armor at or past the `GearArmorSpeedFloor` (-0.5 c/s: plate and
cataphract, never). `Budget` is `GearMaterialBudget`: the supply census less
MaintainResource reserves and holds, the floor food bills honour; a bill option
whose ingredients exceed it is refused (nil is unbudgeted, an unmeasured
material unfunded). Shield belts go to melee soldiers and the medic (highest
work priority Doctor); a psychic foil helmet only after a psychic-drone letter;
smokepop belts are never planned. Once the gear census derives a soldier role
the review latches `Soldiers` and the research roadmap splices
`ArmorResearchRungs` (Smithing, ComplexClothing, FlakArmor, Shields) directly
after Electricity for every EnsureResearch reading.

Each target purchase produces a gap with slot, exact product, source
(loose/stored/bill) and marginal ensemble gain against the current worn slot.
Gaps sort by descending gain then slot. Recovery means no gap above the role
threshold: soldier 0.05, hunter 0.1, slave 0.5, others 0.2. Equal scores retain
worn gear and minimize purchases; otherwise supply preference is loose, stored,
then bill, with stable item IDs. `PlanColonyGear` visits pawn IDs in order and
allocates each physical supply once. `GearProductionDemand` sums only actionable
bill gaps by definition and stuff; quality does not split demand. These are
product quantities, not ingredient reservations or promises of crafted quality.

`ReviewGear` exposes targets and demand when every pawn has a complete model.
The existing method planner projects those gaps into replacement or demand-sized
production methods. Loose/stored targets must still occur in the native eligible
candidate list; native gain admits the item, while model gain orders it. An
unavailable admission stays blocked. Existing production resource checks and
shared Hands execution remain authoritative.

## Dispatch, completion and interruption

The native gear operation requires a paused game, exact pawn and target identities,
and a signature covering load, map, outfit/filter, primary weapon, worn identities
and forced/locked state. It rechecks eligibility at dispatch and starts ordinary
`Wear` or `Equip` work. Drafted, incapacitated, quest and player-ordered pawns are
preserved. Production also rechecks the retained pawn/loadout prerequisite and
available replacements before adding its bill.

On Actions/Apply the apparel order is a `PawnOrderIntent` of kind `WEAR`
(#939, `NativeGearOperations`). Native checks the pawn, the apparel and a
material native gain live, then issues the `Wear` job as ordered (not forced)
work. A pawn already wearing the apparel or walking to it applies again.
Weapons use the `EQUIP` kind.

The `pawn_gear` postcondition requires a later native observation of the exact
item in the pawn's apparel or primary equipment. Delivery of an order cannot
complete it. Lost receipts retain their pre-write timestamp and context; fresh
matching loadout evidence can reconcile success without replay. Changed load or
player direction blocks completion. Interrupted jobs and missing observations
cannot certify success. The shared watchdog bounds lack of progress.

A bill receipt only confirms configuration. The maintained deficit remains until
usable gear is observed. A completed bill that produces an unsuitable item does
not trigger unlimited replacement bills under the same loadout prerequisite.

## Acceptance

The gear planner's decisions (#472) replay from colony snapshots recorded
off the former `gear/*` native cases (#748), in
`go/internal/policy/gear_snapshot_test.go`: the season lookahead (#467)
asks every colonist for a cloth parka or jacket plus a tuque before the
first winter twelfth; tainted apparel (#468) moves everyone to the worker
policy and never orders the tainted parka; a stripped twelve-pawn roster
(#469) is dressed from stored spares, each pawn and each item once, with
no bill while an offer stands. The soldier loadout (#470, #471) is covered
by [weapon planner](weapon-planner.md#acceptance). The single
shirt-from-leather path is a colony snapshot test (#894,
`buildingruntime/routine_ladder_snapshot_test.go`): with no tailoring bench the
workshop step builds a HandTailoringBench, and once it stands the gear step
produces the shirt on it from plain leather.

Finished apparel in valid storage is aggregated by definition, stuff, quality
and hit-point band in `GearSnapshot.stored_apparel`, bounded to 4096 rows.
Forbidden and tainted apparel is excluded. Normal-or-better items above 50%
condition offset matching definition/stuff demand before production. A finite
`GearBatch` bill reserves the full batch ingredients and carries its exact filter;
weapon demand joins after colony-wide loose-weapon assignment. Each review admits
at most one bill and independent pawn orders bounded by free development slots;
pawn and item identities cannot be claimed twice by open dressing methods.

Fixture checks, native scripted pawn outcomes and sustained seasonal campaigns
are different evidence levels.

Optional `RoutinePolicy.GearSpareTargets` keeps unworn spares by definition.
These targets bind to `MaintainResource`: its workshop ladder stages missing
benches, and its storage prerequisite creates covered, reachable allow-listed
stockpile space before the standing stock bill. Spares default to disabled.
