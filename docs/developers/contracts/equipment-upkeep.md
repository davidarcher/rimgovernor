# Equipment and apparel upkeep

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md) · [Apparel policy operation](apparel-policy.md) · [Weapon planner](weapon-planner.md)

`MaintainEquipment` is a maintained development concern in the shared ColonyPlan.
Emergencies suspend it. Hands issues its actions; neither the native read nor an
adviser independently starts work. Missing observations remain unknown. Selected ordinary tailoring and construction activities are scheduled by policy; bounded Methods
and pawn/item claims prevent duplicate work. The [pawn profile](work-assignment.md#pawn-profile) exports the
per-pawn apparel and weapon flags (Nudist, Ascetic, Brawler's `MeleeOnly`) the
loadout planner consumes. The role apparel policies are the
[apparel-policy operation](apparel-policy.md); weapon assignment is the
[weapon planner](weapon-planner.md); this page is the loadout model.

## Native observations and selection

Planning `rimgovernor/observations_read_colony_facts` includes `planning.gear`. The separate typed
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
grow with pawns x loose items. MaintainEquipment only wears the best funded
candidate, and the wear order's own census applies the same bound. The census
candidates are apparel only; loose weapons are the equip family's. A
`gear_replace` whose candidate the fresh census no longer offers as apparel, or
whose wear preview refuses `NOT_FOUND`, is cancelled rather than held, so the
concern can select a fresh method at the next review.

## Production and resource protection

The catalog's base work and bench-speed observations remain available to existing
production families. Ordinary clothing instead uses the exact native recipe's
stuff-adjusted work, worker speed, bench speed and routes in the shared activity
comparison described below. Missing estimates remain explicit unknowns.

Available eligible replacements precede production. Clothing demand generates
competing garment recipes, stuffs, quantities and exact input allocations; only
the selected alternative becomes a Method and Hands recipe intent. Existing
supported bills and unfinished garments can be selected for execution. When a
workshop is missing, the existing workshop ladder and research prerequisites
retain their owners. Shared admission protects construction and other committed
materials. The clothing runway below remains an acquisition estimate; it does
not pin the material of a later executable recipe.
Equipment needs include the loadout model gaps:
replacements for damaged apparel, and definition/stuff options that improve an
observed thermal deficit (`cold`, `heat`). A core body-part group (Torso, Legs)
the pawn wears nothing over is a deficit in any weather, found by the brain from the
worn garments' groups. The native producer sends no deficit flag and no
replacement list (`GearLoadout.deficit` and `replacement_needs` are reserved);
every gap is the model's. A definition-level
thermal estimate is a procurement candidate, not proof of the eventual garment's
quality, eligibility or sufficient protection.

The review's apparel-condition census (`GearReview.WornOut`, `Uncovered`) is the
fraction of colonists wearing any garment at or under the 50% tattered threshold
and the fraction with a core group uncovered, derived from the same loadout read;
it is known only when every colonist's worn apparel was observed. With complete
loadout-model inputs, condition and coverage contribute to scored gaps.
Missing research, workshops, materials or suitable definitions remain explicit
blockers. This concern does not invent a trade or bypass native apparel eligibility to obtain
an item. Bench staging uses the equipment concern through the shared workshop ladder.

### Clothing material demand

Clothing is a runway, not a Concern of its own or a standing floor
(`policy.PlanClothingRunway`). Each review projects every worn garment of the
loadout models to cross the 50% tattered threshold within `ClothingHorizonDays`:
the garment's condition fraction above the threshold times its `MaxHitPoints`
stat for its def and stuff, over `ApparelProperties.wearPerDay` (hit points
lost per day; native `TakeWearoutDamageForDay`). A garment already tattered
counts; one without hit points or a wear rate never does. Each such garment,
less the serviceable stored garments covering the same core group, demands its
recipe's ingredient counts (`observation.ClothingGarments`) at the cheapest
member of the bill filter `gearFilter` builds (the loadout stuff and the
stuffs sharing a catalog category with it), as a stock level like
construction demand, kept in the Rounder's `constructionMemory`. The Round's
resource supply plan serves it with the stuffs that filter admits and, for
leather, with hunts priced at the animal's
butchery leather (one candidate per animal whatever else it yields, its labor
charged once and a designated deer's leather counted toward the need). The
demand ranks below the food plan, which has already taken its labor, and is
wanted within `ClothingHorizonDays`, so a field with a lead of days serves it
(`policy.FieldHarvestCandidate`: zone and sowing as upfront work, grow days as
lead, the crop as stock cap) and trade stays a candidate. Sowing a cotton field
is not yet an executed method.

## Loadout model

`policy.PlanGearLoadout` consumes a complete, eligible def × stuff × quality
catalog and worn gear. The native gear census supplies it per pawn
(`GearLoadout.loadout_model`, with the outdoor temperature): traits, every
worn garment, the eligible loose or stored candidates and the producible
definitions whose recipe research is finished (every allowed stuff admitted by the discovered recipe filter), every unworn option uncapped, each an id, def, stuff, quality,
condition and flags (tainted, locked, smoke-pop). Everything def-static is
the catalog's: The brain takes each option's layers and body-part groups,
shield, psychic and move-speed offsets from the ThingDef row, and armor,
insulation and market value from the stat table for its def and stuff (a
stat the game does not show for the def is a stat the garment lacks); the
frame's finished research (`ResearchSnapshot.finished`) gates the bill
options. The pawn row carries the wear inputs the rows cannot say: gender,
developmental stage and the body-part groups the pawn still has a part in.
The brain applies the wear filter (gender, stage, a present part in one of the
garment's groups) to the apparel rows to list the definitions the pawn can
wear; armor is the Soldier outfit
tag without Worker, and a def covers the body with a Torso or Legs group.
The brain derives each option's slot from its layers and groups,
takes the role from the apparel-policy read and narrows unworn options to
the definitions that role's apparel policy permits. A census row the
model cannot be built for (no model, comfortable temperatures, gender or outdoor
temperature, a definition the catalog lacks) or whose bounds the model refuses (for
example two worn garments on one slot) is not judged by a weaker rule: the pawn's
`GearPawn.LoadoutModel` is unknown with the cause in `GearPawn.ModelRefusal`, and
planning the colony fails with an error naming the pawn and the cause. A pawn with no
apparel policy (it cannot wear apparel) is unknown with no cause and is skipped when
blocked, an error otherwise. A frame without the definition catalog, finished research
or outdoor temperature leaves the whole census unknown. This pure the brain model neither discovers
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
slave, prisoner, unrevealed-creepjoiner, incapable-of-violence and drafted-squad
status. Precedence is child, slave, prisoner, non-combatant, soldier, then highest-priority work (stable role-name tie
break). Hunters require ranged range at least 25 and favor warm outerwear;
workers reject movement penalties, indoor workers reduce thermal weight,
soldiers favor sharp then blunt armor with helmets gated by Smithing and
shields restricted to melee, children select Kid/Apparel_Kid definitions, and
slaves, prisoners and unrevealed creepjoiners take the constraint mode:
cover legs and torso, meet min(thermal need, item capacity) from the comfort
band against the seasonal range, then pick minimum acquisition cost (worn gear
is free). Tainted (dead-man) apparel is excluded unless the wearer is
taint-free, slave-only apparel (the def's `slaveApparel`) goes only to slaves,
and weapons are never planned. Garment stats and conflict metadata determine combinations
such as a flak vest beneath a duster; definitions are not hard-coded.

Armor ladder. An option carries its recipe's research and ingredients;
it is eligible only once the input's finished-research census names every
project, so a soldier's gaps progress simple helmet (Smithing), then flak vest
and flak helmet, then flak jacket and pants (FlakArmor), and recon or marine
armor once their research is finished (their materials are open-bill demand). Soldiers
refuse armor at or past the `GearArmorSpeedFloor` (-0.5 c/s: plate and
cataphract, never). The gear planner's holds are `policy.MaterialHolds`, what
standing blueprints and frames still need and live bill jobs have promised.
Shield belts go to melee soldiers and the medic (highest
work priority Doctor); a psychic foil helmet only after a psychic-drone letter;
smokepop belts are never planned.

Each target purchase produces a gap with slot, exact product, source
(loose/stored/bill) and marginal ensemble gain against the current worn slot.
Gaps sort by descending gain then slot. Recovery means no gap above the role
threshold: soldier 0.05, hunter 0.1, slave and prisoner 0.5, others 0.2. Equal scores retain
worn gear and minimize purchases; otherwise supply preference is loose, stored,
then bill, with stable item IDs. The pruned exact search visits at most `policy.GearSearchBudget` (200000) nodes per pawn; past it the best ensemble found is kept (`GearLoadout.SearchBudgetExhausted`), no option is dropped, and the review journals a `gear_search` row. `PlanColonyGear` visits pawn IDs in order and
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
preserved. Selected tailoring rechecks native recipe legality and exact inputs;
the normal equipment review owns the wearer need and physical replacement choice.

On Actions/Apply the apparel order is a `GiveJobIntent` with job `Wear`
(`NativeGearOperations`). Native checks the pawn and the apparel
(vanilla wear validators; reservation and reach at the shared order danger) live, then issues the `Wear` job as ordered (not forced)
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

The gear planner's decisions replay from recorded colony snapshots: the season lookahead
asks every colonist for a cloth parka or jacket plus a tuque before the
first winter twelfth; tainted apparel moves everyone to the worker
policy and never orders the tainted parka; a stripped twelve-pawn roster
is dressed from stored spares, each pawn and each item once, with
no bill while an offer stands. The soldier loadout is covered
by [weapon planner](weapon-planner.md#acceptance). The single
shirt-from-leather path replays from a colony snapshot: with no tailoring bench the
workshop step builds a HandTailoringBench, and once it stands the gear step
produces the shirt on it from plain leather.

Finished apparel in valid storage is aggregated by definition, stuff, quality
and hit-point band in `GearSnapshot.stored_apparel`, bounded to 4096 rows.
Forbidden and tainted apparel is excluded. Normal-or-better items above 50%
condition offset matching definition/stuff demand before production. A finite
`GearBatch` bill reserves the full batch ingredients and carries its exact filter;
weapon demand joins after colony-wide loose-weapon assignment. Each review admits
at most one bill and up to sixteen independent pawn orders;
pawn and item identities cannot be claimed twice by open dressing methods.

When a colonist is unarmed and no bench hosts a weapon recipe, `MaintainEquipment` stands a
free `CraftingSpot` in the planned shelter's template slot while a shelter is planned, and in the
planned workshop once it has left the plan (the workshop's plan interior, built or not; with none
planned the step waits on `workshop_room`). Once another usable bench hosts a weapon recipe the
review sets `CraftingSpotRetireOwed`, which keeps the goal open until the spot is deconstructed.

Fixture checks, native scripted pawn outcomes and sustained seasonal campaigns
are different evidence levels.

These targets bind to `MaintainResource`: its workshop ladder stages missing
benches, and its storage prerequisite creates covered, reachable allow-listed
stockpile space before the standing stock bill. Spares default to disabled.

## Live clothing requirement selection

Ordinary textile/leather garments use the normal `MaintainEquipment` review,
`RoundsGearPlanner`, shared activity comparison, Method journal and Hands.
The equipment loadout model owns demand. Generation reads every eligible
`GearLoadoutInput.Options` garment/stuff option, not only the loadout winner's
`Replacements`. Physical eligible replacements still precede new production.
Nudist, taint, outfit, coverage and climate constraints remain equipment policy.

`ClothingMethodCandidates` exposes transient recipe/delivery candidates to the
shared ColonyForecast comparator before binding. Each observed bench recipe,
allowed stuff, eligible worker, ingredient allocation and storage destination can
remain an alternative. Existing supported bills are exact start/resume options
respecting their worker, filter, radius and storage settings; a standing bill is
not counted as a second product. Running unfinished work supplies a retained
completion estimate. Unfinished work with no active worker can be selected for
resumption using its exact recipe, stuff, creator and bound bill. No loose inputs
are charged again after native observation shows their conversion into a UFT.
Unsupported existing intent remains visible in findings and is not cancelled.

Known clothing effects retain explicit lexicographic priorities:

1. Reduce cold/heat exposure within the loadout model's ambient/seasonal range.
2. Restore missing core coverage or an observed required apparel definition.
3. Replace a tattered worn garment.

Shared requirement IDs cap repeated benefit. Compatible garments can combine;
conflicting garments for one wearer cannot claim simultaneous protection. Each
recipe iteration makes one item; multiple beneficiaries and compatible needs
supply quantity alternatives. Empty selection postpones work. Known current
output can win without a new order. Equal relief prefers lower total participant
labor, including supplied native route costs, ingredient trips/capacity and
remaining recipe work divided by observed pawn/bench speed. Construction demand,
other admitted materials, participant availability, exact stack quantities,
bench claims and output-cell capacity constrain bundles. An exact existing bill's
material claim overlaps its selected iteration once, rather than being charged
again. There is no separate clothing scheduler, reservation store or demand owner.

The supported native family is ordinary single-output, stuff-made textile/leather
apparel using the base recipe worker and one stuff across its ingredient slots.
Armor, weapons, special products and mixed-resource recipes retain their existing
owners or an explicit unsupported finding. This does not migrate unrelated
production. The old local clothing bill/material winner has been removed;
`DeclareGearOrders` retains already standing intents while new garment production
is selected through activities.

Bounds are explicit: native reading has a 512-operation budget; pure allocation
seeds every observed starting stack then searches partial splits with a 128-visit
budget and at most 16 variants per row. Shared generation admits at most 16
outcomes/32 tasks. Truncation is reported, never an optimum claim. A bundle uses
one recipe per bench and distinct output cells; missing between-job routes can
exclude sequential jobs on one worker. Route duration is a native path-cost
estimate, not a simulation of pickup animations or future congestion. Relief uses
observed Normal-quality stats; future crafted quality, future weather, coupled
needs and colony-wide portfolio opportunity costs remain unassessed. Material
scarcity is a hard shared budget, not a calibrated market-price valuation.

`ClothingChoiceTests` covers shape/stuff/distance/scarcity reversals, stack splits,
compatible and conflicting garments, retain/postpone, unfinished work, unknowns,
and journal/Hands/output continuation. `WorkResourcesTests` covers actual UFT
conversion without double charging loose inputs. The prepared native cases
`production/clothing-activity` and `production/clothing-activity-unfinished`
enter through normal review without a fixture-selected recipe, bill or material;
they assert exact selected-stuff creation, conservation, observed release and
interrupted UFT resumption. Registration/compilation is preland evidence; gameplay
is supplied by the nightly. Existing food, coordination, physical-wear and armory
cases remain. The former local-winner ClothingChoice tests were replaced because
they asserted the removed selection path, not the new runtime outcome.
