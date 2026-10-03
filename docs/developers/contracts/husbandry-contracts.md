# Animal husbandry contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`MaintainHerd` creates a persistent, player-owned `MaintainHerd-<race>` goal in
ColonyPlan. It sizes each race itself (#875); there are no operator flags.

**Race catalog (#1625).** Static per-race facts (carrying capacity, trainability
and trainables, wildness, body size, combat power, market value, minimum
handling skill, and products with item and interval) come from one native read,
`rimgovernor/observations_read_animal_race_catalog`, taken once per load token
and held in Go memory as `policy.AnimalRaceCatalog`. It covers every animal race
the game knows, wild or tame, and an unread fact is unknown, never zero. It is
derived state: no save, journal or per-animal copy. The routine reading
requires the catalog: an unreadable catalog fails the reading loudly like any
other required native read, and the herd plan never runs without it.

- **Herd plan (#1628).** `policy.PlanHerd` derives every race's job each cycle
  from colony facts and the race catalog; nothing is stored. A job (milk, wool,
  chemfuel, food = eggs, haul, war) is wanted while a kept animal holds it: a
  catalog product, or a learned `Haul` / `Release` (attack) training. Its
  candidates are the catalog races able to hold it that the colony can obtain:
  owned, tameable and non-dangerous wild animals on the map, or offered (a
  trader or reward, `HerdPlanInput.Offers`). They rank by yield per body size
  (body size stands for feed), then cows first for milk, then lower
  `minimum_handling_skill`, then name. The best is the job's target; the plan
  re-ranks every cycle as availability changes. A race with an unread yield or
  size cannot hold or be ranked for the job that needs it. Other roles: companion (a bonded animal on a race with no work job) and
  none.
- **War animals (#1637).** Unlike the other jobs, `war` is also wanted before
  any animal holds it: while the wealth budget is read and has no negative
  headroom (defense capacity keeps pace with the wealth the animals add), the
  plan targets a pair of the race with the highest catalog combat power (not
  per body size) that the colony can obtain and a handler clears
  (`TamerFor` at the catalog `minimum_handling_skill`; a thrumbo needs its
  catalog minimum, an unread roster or minimum leaves a race out). Negative or
  unread headroom plans no new war animal; kept ones stay and the budget
  ceiling cuts their race. Taming follows the target's floor; training and
  masters follow the war job, and the animals fight through the existing
  animal combat orders (`combat_animals.go`).
- **Floor.** The target race's floor is its head count: the larger of a
  breeding pair, the capacity the other holders give today (adult yield, or
  trained carrying capacity or combat power) and the food plan's
  productive-animal terms, divided by one adult's yield. Below it, a
  tameable wild animal of that race is designated (`tame`; the pick is ranked
  below) while
  `MaintainAnimalFeed`'s review reports no shortfall and a
  [handler](work-assignment.md#situational-roles) (`TamerFor`) clears its
  `minimum_handling_skill`. Predators and races with
  `manhunterOnTameFailChance ≥ 0.2` are never tamed, except grizzly and polar
  bears and wargs. A race that is not a target has no floor.
- **Cap.** The target's cap is its floor plus one breeding group (3). The
  ceilings are the wealth budget: while `WealthBudget` headroom is negative,
  every cap scales by `(wealth + headroom) / wealth` (a race with no job is held
  to its current count scaled, never below 6; a target never below 3); with
  non-negative headroom there is no wealth cap and no flat per-race cap. And,
  for pen animals, the pasture cap `floor(n × B / D)`: `B` is the pens'
  worst-quadrum pasture plus stored feed spread over 15 days, `D` their grazing
  demand, `n` the race's penned count (RimWorld's pen capacity is this same
  nutrition balance, `PenFoodCalculator`). Predators are never penned; they eat
  meat and stay under the stored-food feed gate. An unknown budget or pen facts
  leave that ceiling out.
- **Founder.** A preferred race with animals but no breeding pair (a lone
  animal, or one sex only) is a founder: never culled or sold, no cap, and
  `HerdRole.WantMale` / `WantFemale` name the missing sex for taming (#1629)
  and buying (#1636). No sterilize applies to a founder.
- **Retirement.** A holder that is not the target keeps working until the
  target's adults cover the job: the target has a breeding pair and its adult
  capacity is at least the holder's (trained capacity counts only animals that
  learned the training, so small haulers retire only after larger ones learn
  Haul). Then, if it holds no other kept job, the race is `Retiring`: cap 0, no
  breeding pair kept, `HerdPolicy.Retired`; its animals are surplus (slaughter
  or release below; selling is #1632). A holder that keeps working only until
  a better race covers its job is `HerdRole.Superseded`: it is sterilized
  (below), not culled. A bonded animal is never removed (native
  `SafeToSlaughter`).
- **Plan output.** `HerdPlan.Roles` (job, preferred, founder, wanted sex,
  retiring) and `HerdPlan.Jobs` (ranked obtainable races, target, head count)
  are what the taming, training, sterilize, sale, purchase and pen issues read;
  `HerdPlan.Policy` is the `HerdPolicy` band the `MaintainHerd-<race>` goals
  execute.
- **Sale (#1632).** `HerdSaleAnimals` lists the animals over a race's ceiling
  (all of a retired race) that are known unbonded and, outside a retired race,
  not trained for work; founders have no ceiling and never sell, and any
  unknown designation or bond sells nothing. A race the player ideo bars from
  slaughter (see Removal) is never sold. While `SilverShort` holds
  (`AnimalSaleNeed` sets `TradeNeed.SurplusAnimals`, which opens a caravan),
  `SelectTrade` sells each listed animal through its own pawn trade row
  (`TradeSheetRowFact.PawnID`, line count -1) after the resource and art
  lines; silver reserve rules are unchanged. Staging passes `allow_pawns`;
  native `AcceptTrade` exports a pawn row only when it is an unbonded
  player-faction animal and the deal's floors name its definition. Native's
  preview stays the authority.
- **Purchase (#1636).** The trader's pawn rows of catalog races are the plan's
  `Offers` (`HerdOffers`), so a map with no cows can still plan milk.
  `HerdWants` lists what the plan lacks: each job's target race, then each
  founder's missing sex. `SelectAnimalPurchase` buys the first want with an
  affordable pawn row (trader holds it and will trade, colony side none, race
  and sex match, `TradeLine.pawn_gender` read natively from the pawn, cheapest
  then line id) as a second purchase line beside `SelectPawnPurchase`, within
  the same budget (`PawnPurchaseFraction` of silver, above the reserve and
  the lines already selected). No joiner-capacity gate and no `allow_pawns`
  (only giving a pawn away needs it). An unreadable race catalog fails the
  selection.
- **Removal.** Above the cap, surplus goes by `slaughter` whenever native
  `SafeToSlaughter` allows it (not bonded, no master, not pregnant, not
  designated) and the player ideo neither venerates the race nor has an
  `AnimalSlaughter` precept; otherwise by `release` when `SafeToRelease`
  allows it (same exclusions). The same bar excludes the race from food
  slaughter and from the Prioritize order. `AnimalState.slaughter_barred` is
  read natively every census (`false` without Ideology); an unread value
  fails the removal, sale, food and Prioritize choices (no fallback). Cull order: old (past 80% of
  `lifeExpectancy`) or sick; males beyond one per five females; untrained
  adults by highest grazing demand per meat; adults that learned Haul, Rescue
  or Release (attack); juveniles only while pasture is short (`B < D`). Ties
  go to the lowest ID. Races without a cap are never culled for surplus.
- **Breeding pair.** No removal leaves a race with fewer than one male and two
  females, except a retiring race; an animal of unknown sex is never removed.
  Only fertile animals make the pair: a sterilized animal (`AnimalState.sterilized`,
  `UpkeepAnimal.Sterilized`) counts toward a race's head count and cap but not
  toward its pair, in removal, the founder rule (`paired()`) and sterilize
  itself. Standing designations
  that would break the pair, or no longer match a surplus or an open food
  offer, are cancelled.


## Methods

Every method is one `HusbandryIntent` on Actions/Apply: a direct settings
write on one exact animal that native validates against live state when it
applies, so applied is the effect; an order that already holds applies again.
Native handler labor afterwards (taming, walking an animal off-map, training
steps) is observed through the animal's own state, never inferred from the
receipt. `cancel_slaughter` and `cancel_release` remove a standing designation.

| Method | Target | Native write | Completed when |
| --- | --- | --- | --- |
| `train` | player animal | `SetWantedRecursive` | trainable still wanted (learned state tracked separately) |
| `slaughter` | player animal | `Slaughter` designation | designation present |
| `prioritize_slaughter` | player animal with a standing slaughter designation | `GiveJobIntent` `Slaughter`, prioritized, by the best Animals-skilled Handling-capable colonist (argument: handler id); no draft, no flag write | handler takes the job; the designation is consumed by the slaughter |
| `release` | player animal | `ReleaseAnimalToWild` designation | designation present |
| `tame` | wild animal | `Tame` designation | designation present, or the animal now reads as a player animal (the taming job consumed it) |
| `sterilize` | player animal | `HealthCardUtility.CreateSurgeryBill` of the sterilize recipe, found live on the animal's `def.AllRecipes` (whole-body, adds `HediffDefOf.Sterilized`); no argument, no fallback recipe | bill queued; the animal's `sterilized` reads back once the surgery has run (receipt: `AnimalEffect.sterilized`, `sterilize_queued`) |
| `allowed_area` | player animal | `AreaRestrictionInPawnCurrentMap` (argument: area id, empty clears) | animal's allowed area reads back equal |
| `master` | player animal | `PlayerSettings.Master` (argument: colonist id, empty clears) | master reads back equal |
| `follow_drafted` | player animal | `followDrafted` (argument: `true`/`false`) | flag reads back equal |
| `follow_fieldwork` | player animal | `followFieldwork` (argument: `true`/`false`) | flag reads back equal |

Apply-time rules: `sterilize` fails loudly when no recipe on the race adds the
Sterilized hediff, or the recipe is not available on the animal now;
`allowed_area` needs `SupportsAllowedAreas`; `master` and the
follow flags need `Obedient` (learned Obedience; native refuses otherwise). An
area or master id the map does not carry is refused as not found. Each follow
method writes only its own flag.

**Tame ranking (#1629).** Candidates are ordered by the plan, not by ID: the race
the plan wants most (a preferred race, then job order milk, wool, chemfuel,
food, haul, war; a race with no job last), then within a race the sex a founder
is missing (`WantMale`/`WantFemale`; the wild census carries `Gender`), then
lower `manhunterOnTameFailChance`, then lower `minimum_handling_skill`, then ID.
A candidate no handler can tame is skipped for the next. `HerdPolicy.Roles`
carries the plan's roles to this ranking.

**Handler leveling (#1634).** While a target race is short of its head count and
its catalog `minimum_handling_skill` is above what every handler has
(`TamerFor` finds none), the plan names one easy race to level on
(`HerdPlan.Leveling`): the first of alpaca, boar, hare, husky, labrador with a
tameable wild animal on the map that a handler already clears. It gets a floor
of 1 (taming it, then the usual training, raises the Animals skill). Once a
handler clears the wanted race, or no easy wild animal exists, the plan carries
no leveling race. The easy animal has no retirement rule yet.

**Master assignment (#1635).** After removal, `HerdMasterChoice` assigns masters
bond first, for any planned, non-retiring race:

1. A bonded animal is mastered by its bonded colonist: `AnimalState.bonded_pawn_ids`
   lists the living humanlike pawns the animal has a Bond relation with
   (colonist, prisoner, slave, guest or a pawn who left); the planner keeps
   those on the colony roster, the first by id (a roster partner already
   mastering is kept). Bonds give +5 mood as master, -3 otherwise.
2. An unbonded animal with a wanted, available, unlearned trainable
   (`herdTrainQueue`) gets the handling-capable colonist with the best Animals
   skill; any handling-capable master is kept. One colonist may master any
   number of animals. Follow flags are not written.
3. An unbonded war animal gets a front-line handler (`FrontLine`,
   handling-capable) and `follow_drafted` true.
4. An unbonded haul animal gets no master and no follow flag (haul training
   hauls on its own).

`follow_fieldwork` and the other follow flag are never written. The census
carries `master_id`, the follow flags and `obedient` on `UpkeepAnimal`. An
animal is skipped unless obedient (native refuses master and follow without
learned Obedience), when marked for release or slaughter, when its race is
retiring or unplanned, or when its master, release, slaughter or a trainable
fact it needs is unread. A master who fits is kept; the master is rewritten
only when empty or not fitting, then `follow_drafted` for a war animal, one
write per cycle. Native reports `master_id` and
`bonded_pawn_ids` as `GetUniqueLoadID()`, the same id as the pawn rows. The sale guard (`HerdSaleAnimals`) never sells a bonded animal, nor a
mastered animal outside a retired race.

**Sterilize (#1631).** `SterilizeChoice` wants an animal sterilized when its race
is `Superseded` (it keeps working but stops breeding until it retires) or it is
a male beyond the plan's ratio (one per five females, `herdMalesPerFemales`) of
a race kept within its cap. Never a founder, a retiring race (culled instead),
an animal designated for removal, or one with an unread `sterilized` or
`sterilize_queued` fact; and never a sex below its breeding pair of fertile
animals (1 male, 2 females), which must exist to begin with. A queued bill
counts as sterile, so bills cannot take a race below the pair. It runs only
while `RoutineFacts.VetRoom` is ready (a vet room reservation with a built
medical animal bed, plus the allowed-area id covering it); an unknown or
unready vet room selects nothing, and the layout does not expose it yet. The
steps are derived each cycle from the animals' allowed area and sterilize
facts, one write per cycle and one animal in the room at a time: `allowed_area`
into the vet room, `sterilize`, then `allowed_area` cleared once sterilized
(also for an animal in the room that is no longer wanted and has no bill). An
animal with a bill queued is waited on. Clearing the area does not restore an
earlier restriction.

Selection order each cycle is train, then tame, then surplus removal, then
master assignment, then sterilize; one write
per cycle. The recovery planner also produces `allowed_area` changes from fresh
Auto safety facts: a roofed refuge during roof hazards, otherwise unrestricted
food/work access. It skips pen-managed animals and unknown area/safety facts.
Other settings methods remain available through the same husbandry action.
Pen containment is not a husbandry method: pens are built by
`MaintainAnimalContainment` and native handlers rope pen animals into any
suitable pen on their own.

## Ownership and population

The goal belongs to the colony and map where it was accepted. Each native request
names the exact animal and applies under native authority against the live
animal. A refusal re-plans from the next review; an unknown outcome resends
the same order.

Surplus and shortfall counts subtract animals already designated for
slaughter or release and add wild animals already designated for taming, so a
pending write is never duplicated. Native eligibility is checked again at
apply: bonded, mastered, pregnant, downed and already-designated animals are
excluded from removal by RimWorld's own designator rules plus the master/bond
exclusions `SafeToSlaughter`/`SafeToRelease` add; a tame candidate must pass
`TameUtility.CanTame` and carry no tame or hunt designation. Masters, allowed
areas, following, sterilization and breeding separation are ordinary game
settings: while native authority reads Auto the controller may change any of
them, including ones the player just set (see the working agreement). Masters,
areas and following are written through the methods above; sterilization is
not yet written.

Births are never counted as pending: a shortfall with no tameable wild animal
of the race on the map simply reports no candidate until one appears. Renewing
a target cancels its uncompleted controller steps; cancellation leaves
previously issued game orders in place, following the shared goal cancellation
contract.

## Training and products

Training requests use native `CanAssignToTrain` and `SetWantedRecursive`; native
`learned` and step counts track progress separately from settings receipts.
Removal-designated animals receive no training changes. Training follows the
animal's herd-plan job: a hauler learns Obedience then Haul, a war animal
Obedience, attack (`Release`) and Rescue as the race allows, any other job
Obedience, and a retiring or job-less race nothing. A learned skill that decays reads as
unlearned and is requested again; the native read exposes only the learned
flag, not the step count or time to decay.

Handling joins shared deterministic work allocation with the observed native minimum
skill. Player work overrides remain authoritative. The herd observation retains safe
handler reachability, current priorities, jobs and targets, alongside training target
count and ready product count. Normal native handlers perform training, milking and
shearing. No instant training, forced product generation or alternative job executor
exists. A full udder or wool comp is pending work, not a collected product.

## Feed capacity and containment

Reserve days are a planning horizon supplied by the player, including seasonal needs.
The shared food forecast allocates current stored food among native eligible eaters
using diet, policy, safe reachability, held stock and rot deadlines. Missing reads,
grass, expected harvest and unborn animals cannot certify feed capacity. Animal pen
membership uses the native enclosed, suitable pen lookup; a marker by itself is not
containment. Area-managed animals use their current allowed-area membership.

An explicit feed resource, or observed feed used exclusively by animals, creates an
owned `MaintainResource` goal through the existing acquisition/production methods.
The stock target uses native per-item nutrition and demand from competing eligible
eaters. Missing suitable feed, production prerequisites, handlers, access or storage
remain visible blockers. Cancelling the herd stops its unissued feed work. Acquiring
stock does not establish that animals can reach or have consumed it.

These are current-condition projections. Future births, changing temperatures,
spoilage and native job selection require fresh review; a bounded acceptance run
does not establish indefinite herd sustainability.

See husbandry acceptance for the native fixture
and the distinction between setup, orders and pawn outcomes.

## Food channel planning

The tick food plan aggregates native milk and egg production by race. Rates use
native resource nutrition, comp intervals and body-resource growth speed;
inactive producers contribute nothing, and missing rate or reachability facts
remain unknown with an explanation. Milk costs 400 native gather work units per
cycle; eggs need no handler gathering. These costs share the food-plan labor
budget. Full production comps have zero lead time. Projected products never
increase stored-food runway before normal native jobs produce them.

An admitted productive race that beats the marginal crop's nutrition per work
(`FoodHerdPolicy`) gives its job demand: its `productive_animals` term is a head
count the herd plan sizes the job's target race for, so the floor lands on the
best race for the job, not necessarily the race the channel was observed on.
The portfolio explains `MaintainHerd-<race>` and its effective floor. Both herd
review and dispatch use the plan's policy, including the existing feed gate
before taming.

`MaintainAnimalFeed` compares the seasonal harvest gap with each enclosed pen's
native worst-quadrum pasture rate and stored feed. A negative balance can add a
`Plant_Haygrass` field through the existing soil planner, zone preview and Hands
admission. Existing hay-field capacity offsets new planting; a short season or
unknown capacity refuses that method. Hay stays human-inedible and out of human
food channels and human runway. Growing joins the feed goal's labor profile.

While the food runway is below target (a food-plan gap), eligible animals
above max(floor, breeding pair) rank by native meat nutrition per daily
grazing demand, then shorter reproduction interval, then animal ID. Missing
cost facts exclude the candidate. The ledger budgets one offered animal as a
hunt-kind channel with a `slaughter:` ID and 180 native slaughter ticks;
`MaintainHerd` dispatches it through its normal husbandry action.

