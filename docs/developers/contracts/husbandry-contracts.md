# Animal husbandry contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`MaintainHerd` creates a persistent, player-owned `MaintainHerd-<race>` concern in
ColonyPlan. It sizes each race itself; there are no operator flags.

**Race catalog.** Static per-race facts (carrying capacity,
trainability and trainables, wildness, body size, combat power, market value,
minimum handling skill, life expectancy, the manhunter chances and products
with item and interval) are derived from the definition catalog's race rows
(`DefinitionCatalog.AnimalRaces`, built once per load token and held in Go
memory as `policy.AnimalRaceCatalog`); there is no separate native read. It
covers every animal race the game knows, wild or tame, and an unread fact is
unknown, never zero. It is derived state: no save, journal or per-animal copy
(an animal row carries none of its race's numbers). An unbuildable race table
fails the reading loudly like any other required native read.

The race row also carries the game-computed husbandry facts of #2238 (one
census, no later rounds): `adult_min_age_ticks` and the first reproductive,
milkable and shearable stage ages (`RaceFacts`), the tameness decay flag and
period, the wildness tame-chance factor, and the meat def and amount, and the adult feed per day; the
animal interaction job constants (talk and feed ticks, feeds, feed nutrition
share and cap, minimum train interval) are `CatalogConstants`, held as
`AnimalRaceCatalog.Interaction`. Taming and training jobs, milk, wool and egg
rates, leather and butcher yields come from the def rows and stat table as
before. Raw facts only; policy decides.

- **Herd plan.** `policy.PlanHerd` derives every race's job each cycle
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
- **War animals.** Unlike the other jobs, `war` is also wanted before
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
  the animal feed runway reports no shortfall and a
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
  `HerdRole.WantMale` / `WantFemale` name the missing sex for taming
  and buying. No sterilize applies to a founder.
- **Retirement.** A holder that is not the target keeps working until the
  target's adults cover the job: the target has a breeding pair and its adult
  capacity is at least the holder's (trained capacity counts only animals that
  learned the training, so small haulers retire only after larger ones learn
  Haul). Then, if it holds no other kept job, the race is `Retiring`: cap 0, no
  breeding pair kept, `HerdPolicy.Retired`; its animals are surplus (slaughter
  or release below; see Sale). A holder that keeps working only until
  a better race covers its job is `HerdRole.Superseded`: it is sterilized
  (below), not culled. A bonded animal (`Bonded`, `bonded_pawn_ids`) is never
  removed: `herdRemovalMethod` returns none for it (so surplus slaughter,
  release, food slaughter and sale skip it and pick an unbonded animal of the
  race). An unread
  bond fails the removal choice as unknown.
- **Plan output.** `HerdPlan.Roles` (job, preferred, founder, wanted sex,
  retiring) and `HerdPlan.Jobs` (ranked obtainable races, target, head count)
  are what the taming, training, sterilize, sale, purchase and pen issues read;
  `HerdPlan.Policy` is the `HerdPolicy` band the `MaintainHerd-<race>` concerns
  execute.
- **Sale.** `HerdSaleAnimals` lists the animals over a race's ceiling
  (all of a retired race) that are known unbonded and, outside a retired race,
  not trained for work; founders have no ceiling and never sell, and any
  unknown designation or bond sells nothing. The ideology slaughter bar does
  not apply to sale. While `SilverShort` holds
  (`AnimalSaleNeed` sets `TradeNeed.SurplusAnimals`, which opens a caravan),
  `SelectTrade` sells each listed animal through its own pawn trade row
  (`TradeSheetRowFact.PawnID`, line count -1) after the resource and art
  lines; silver reserve rules are unchanged. Staging passes `allow_pawns`;
  native `AcceptTrade` exports a pawn row only when it is an unbonded
  player-faction animal and the deal's floors name its definition. Native's
  preview stays the authority.
- **Purchase.** The trader's pawn rows of catalog races are the plan's
  `Offers` (`HerdOffers`), so a map with no cows can still plan milk.
  `HerdWants` lists what the plan lacks: each job's target race, then each
  founder's missing sex. `SelectAnimalPurchase` buys the first want with an
  affordable pawn row (trader holds it and will trade, colony side none, race
  and sex match, `TradeLine.pawn_gender` read natively from the pawn, cheapest
  then line id) as a second purchase line beside `SelectPawnPurchase`, within
  the same budget (`PawnPurchaseFraction` of silver, above the reserve and
  the lines already selected). No joiner-capacity gate and no `allow_pawns`
  (only giving a pawn away needs it). An unbuildable race catalog fails the
  reading.
- **Acquiring animals for food.** `policy.TameFoodChannels` and `AnimalPurchaseFoodChannels` offer the best wild animal to tame and, per recorded trader, the best live animal it sells, as `Tame` / `AnimalBuy` one-animal candidates beside slaughter, only while the food gap is positive. The yield is the race's milk and eggs net of the animal's feed after the lead (the first milkable or reproductive stage less its age), else its meat net of feed until adult as a one-shot stock. Feed per day is an owned adult of the race, else the owned adults' feed per body size times the race's body size (the game's hunger rate is not mirrored per race), so no owned animal leaves it unknown and offers nothing. Taming is priced as the expected attempts (1 over `TameChanceFactor`; the tamer's own chance is unread) of three talk toils and the feeds each (game structure, `animalInteractionTalks`), with the race's manhunter chance a revenge risk; a purchase is its silver as upfront work with lead 0 (the sheet has no age, a trader's animal is read as adult). Steady gathering work is unpriced (0). Herd room is the race's ceiling when it has one; retired races, a short feed forecast, no capable handler and dangerous races offer nothing. The executors stay: `FoodTameChoice` tames the animal the plan opened (the `MaintainHerd` husbandry step) and `PlannedAnimalPurchases` adds the opened races to the herd wants `SelectAnimalPurchase` fills at the trade sheet.
- **Removal.** Above the cap, surplus goes by `slaughter` whenever
  `UpkeepAnimal.SafeToSlaughter` allows it (policy over native's raw `AnimalState` flags `downed`,
  `in_mental_state`, `pregnant`, `colonist_bonded`, `slaughter_designatable`, plus `master_id` and the
  release designation: not bonded, no master, not pregnant, not downed, not designated) and the player ideoligion's precepts do not bar it; otherwise by `release` when `SafeToRelease`
  allows it (same exclusions). The bar is `policy.ActionStance` ([ideology contracts](ideology-contracts.md#precept-rule))
  applied once per frame (`ApplyHerdPrecepts`) to the history events slaughter raises:
  `SlaughteredAnimal`, plus `SlaughteredVeneratedAnimal` for a race `AnimalState.venerated` names.
  Penalised or forbidden bars slaughter (`HerdFacts.SlaughterBarred`); the same bar excludes the race
  from food slaughter and from the Prioritize order. Food slaughter also needs `EatingBarred` clear:
  `AteMeat`, plus `AteVeneratedAnimalMeat` for a venerated race, must not be penalised or forbidden.
  Event names are `HistoryEventDefOf` fields; effects come from the catalog's precepts. Without
  Ideology defs nothing is barred; with them, an unread ideoligion or veneration leaves the bar
  unknown and fails the removal, food and Prioritize choices (no fallback). Selling is not barred:
  the game raises no history event for selling an animal. Per-race meat kinds (insect meat) have no
  per-race eating read on the wire, so only `AteMeat` and venerated meat bind. Cull order: old (past 80% of
  `lifeExpectancy`) or sick; males beyond one per five females (a fertilizable egg-laying race follows the rooster rule below); untrained
  adults by highest grazing demand per meat; adults that learned Haul, Rescue
  or Release (attack); juveniles only while pasture is short (`B < D`). Ties
  go to the lowest ID. Races without a cap are never culled for surplus.
- **Rooster ratio.** A race with an egg layer that has a fertilized def gets a
  `HerdPolicy.Layers` entry from `PlanHerd`: `HensPerRooster` = (24 / `mateMtbHours`)
  / (eggs per lay / `eggLayIntervalDays` / `eggFertilizationCountMax`), every egg wanted
  fertilized. Below the hen target (fertile females < `PopulationMin`) the race keeps
  `ceil(hens / HensPerRooster)` fertile males, at target one; males beyond that are
  excess (`herdSurplusCandidates`, sterilize) and the kept count is also the male
  removal floor (`ReconcileHerdRemoval` too). Hatching is controlled by rooster count
  only. An unknown mating, count, interval or fertilization fact makes the removal
  result unknown (nothing removed, no sterilize), never a literal fallback. A race
  without a fertilizable egg comp keeps the one-per-five rule.
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

Standing slaughter, release and tame designations lend game time to vanilla
handlers. The governor does not assign a prioritized slaughter job.

Training selection reads availability, learned state and the native wanted flag.
A wanted, unlearned trainable remains a herd deficit, lends game time and needs
no further write. An unknown wanted flag refuses selection until read. Method
identities count prior writes independently of the eight-refusal budget for each
animal and trainable; successful requests do not consume that budget.

| Method | Target | Native write | Completed when |
| --- | --- | --- | --- |
| `train` | player animal | `SetWantedRecursive` | trainable still wanted (learned state tracked separately) |
| `slaughter` | player animal | `Slaughter` designation | designation present |
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

**Tame ranking.** Candidates are ordered by the plan, not by ID: the race
the plan wants most (a preferred race, then job order milk, wool, chemfuel,
food, haul, war; a race with no job last), then within a race the sex a founder
is missing (`WantMale`/`WantFemale`; the wild census carries `Gender`), then
lower `manhunterOnTameFailChance`, then lower `minimum_handling_skill`, then ID.
A candidate no handler can tame is skipped for the next. `HerdPolicy.Roles`
carries the plan's roles to this ranking.

**Handler leveling.** While a target race is short of its head count and
its catalog `minimum_handling_skill` is above what every handler has
(`TamerFor` finds none), the plan names one easy race to level on
(`HerdPlan.Leveling`): the first of alpaca, boar, hare, husky, labrador with a
tameable wild animal on the map that a handler already clears. It gets a floor
of 1 (taming it, then the usual training, raises the Animals skill). Once a
handler clears the wanted race, or no easy wild animal exists, the plan carries
no leveling race. The easy animal has no retirement rule yet.

**Master assignment.** After removal, `HerdMasterChoice` assigns masters
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

`follow_fieldwork` is never written. The census
carries `master_id`, the follow flags and `obedient` on `UpkeepAnimal`. An
animal is skipped unless obedient (native refuses master and follow without
learned Obedience), when marked for release or slaughter, when its race is
retiring or unplanned, or when its master, release, slaughter or a trainable
fact it needs is unread. A master who fits is kept; the master is rewritten
only when empty or not fitting, then `follow_drafted` for a war animal, one
write per cycle. Native reports `master_id` and
`bonded_pawn_ids` as `GetUniqueLoadID()`, the same id as the pawn rows. The sale guard (`HerdSaleAnimals`) never sells a bonded animal, nor a
mastered animal outside a retired race.

**Sterilize.** `SterilizeChoice` wants an animal sterilized when its race
is `Superseded` (it keeps working but stops breeding until it retires) or it is
a male beyond the plan's ratio (one per five females, `herdMalesPerFemales`; the rooster rule for a layer race) of
a race kept within its cap. Never a founder, a retiring race (culled instead),
an animal designated for removal, or one with an unread `sterilized` or
`sterilize_queued` fact; and never a sex below its breeding pair of fertile
animals (1 male, 2 females), which must exist to begin with. A queued bill
counts as sterile, so bills cannot take a race below the pair. It runs only
while `RoundsFacts.VetRoom` is ready: `Ready` is `policy.VetRoomReady` (a vet
room of the plan standing shelled with a standing animal bed the sleeping
census reads medical; unknown while a standing bed's census row or flag is
unread), read by the husbandry planner from the room census; `Area` is the id
of the bot-owned `VetRoom` allowed area (the room interior, planned by
MaintainShelter beside the Safe area, which leaves the vet room out; `""`
until created). An unknown or unready vet room selects nothing. The
steps are derived each cycle from the animals' allowed area and sterilize
facts, one write per cycle and one animal in the room at a time: `allowed_area`
into the vet room, `sterilize`, then `allowed_area` cleared once sterilized
(also for an animal in the room that is no longer wanted and has no bill). An
animal with a bill queued is waited on. Clearing the area does not restore an
earlier restriction.

**Exposure shelter.** A pen animal (`requires_pen`, supports allowed
areas, not marked for release or slaughter) is sheltered while its race is in
danger outdoors: `RoundsFacts.AnimalShelterChoice` runs `AnimalExposures`
over the pen animals' races (an active `ColdSnap`, `HeatWave` or
`ToxicFallout` condition, or the observed outdoor temperature outside the
race's comfortable range, `ComfyTemperatureMin`/`Max` read from the def
mirror into `AnimalRace.Comfort`; no proto change, native read or def-name
list). An animal with no area restriction is let into the bot-owned `Barn`
allowed area (`BarnAreaKey`, the interior of a standing planned barn,
planned by MaintainShelter beside `VetRoom`; `""` until a barn stands, which
chooses nothing). Once its race is out of danger an animal in the `Barn` area
is released with `allowed_area` cleared: the pen is re-derived from the
animal's current area each cycle, no prior area is stored. An animal in any
other area (the vet room) is left to the flow that put it there. A race with
no comfort range, a race outside the catalog, or an unread condition census
or temperature fails the review with `ErrAnimalExposure`. The check also keeps
MaintainHerd in deficit while a write is owed, and it comes before every
other husbandry choice since the animals die of the exposure.

**Threat shelter.** A hostile threat endangers every pen animal:
while `RoundsFacts.Hostiles > 0` (the emergency census's unsafe-threat holds,
that is live, discovered, engaging, non-distant Hostile, HuntingPredator and
hostile-building rows, plus pending drop pods) `AnimalShelterChoice` lets each
unrestricted pen animal into the `Barn` area, one per cycle, without reading
the condition census, temperature or race comfort range; once `Hostiles` is
zero the weather exposure above decides, so a sheltered animal is released
only when both are clear. An unread hostile count is `ErrAnimalExposure`.
What else covers a threat: `PlanSheltering` shelters colonists and non-pen
animals in the Safe area (pen animals are excluded there), and
`combat_animals.go` uses colony animals as defenders; nothing else protects
penned animals. Combat orders still do not skip sheltered animals.

Selection order each cycle is exposure shelter, then train, then tame, then
surplus removal, then master assignment, then sterilize; one write
per cycle. The recovery planner also produces `allowed_area` changes from fresh
Auto safety facts: a roofed refuge during roof hazards, otherwise unrestricted
food/work access. It skips pen-managed animals and unknown area/safety facts.
Other settings methods remain available through the same husbandry action.
Pen containment is not a husbandry method: pens are built by
`MaintainAnimalContainment` and native handlers rope pen animals into any
suitable pen on their own.

## Ownership and population

The concern belongs to the colony and map where it was accepted. Each native request
names the exact animal and applies under native authority against the live
animal. A refusal re-plans from the next review; an unknown outcome resends
the same order.

Surplus and shortfall counts subtract animals already designated for
slaughter or release and add wild animals already designated for taming, so a
pending write is never duplicated. Native eligibility is checked again at
apply: bonded, mastered, pregnant, downed and already-designated animals are
excluded from removal by RimWorld's own designator rules plus the master/bond
exclusions policy's `SafeToSlaughter` and native's `SafeToRelease` add. The slaughter
order itself keeps only physical validity (alive, ours, `Designator_Slaughter` acceptance); a tame candidate must pass
`TameUtility.CanTame` and carry no tame or hunt designation. Masters, allowed
areas, following, sterilization and breeding separation are ordinary game
settings: while native authority reads Auto the controller may change any of
them, including ones the player just set. Masters,
areas, following and sterilization are written through the methods above.

Births are never counted as pending: a shortfall with no tameable wild animal
of the race on the map simply reports no candidate until one appears. Renewing
a target cancels its uncompleted controller steps; cancellation leaves
previously issued game orders in place, following the shared concern cancellation
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
skill. Milking and shearing are the `Milk` and `Shear` work givers of the Handling
work type (WorkGiverDefs), and the gather speed and yield stats (`AnimalGatherSpeed`,
`AnimalGatherYield`) scale with the Animals skill alone. While the herd plan
holds a milk or wool job (`HerdPlan.Jobs`), Handling has demand for one owner
(`WorkDemand.Handling`), so the best Animals pawn the planner finds capable owns
Handling at priority 1 even when nobody is a natural specialist. Player work overrides remain authoritative. The herd observation retains safe
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
containment. The pen is the wall's yard (below), claimed by one `PenMarker`; there is no separately fenced pen and no herd-size bound on containment. Area-managed animals use their current allowed-area membership.

**Wall yard.** The defensive wall's yard (the gap between the core footprint and the ring, `perimeterGap` at least) is sized at plan time to hold the herd's grazing: `LayoutPlan.YardCells` latches `YardCells(HerdPlan.YardAnimals())` (the misc unit's herd plus every herd unit's ceiling) times `yardCellsPerAnimal` (24, an estimate; capacity itself stays the native pen food calculation). The yard grows from `perimeterGap` by the smallest gap whose ring around the core holds that many cells, capped at `perimeterGap + 12`, and `outerClear` widens with it. Derivation sets it once and a replan never resizes it; enlarging a standing wall is #2237.

**Herd units.** Barns and vet areas are sited as units. The misc unit (the first barn and vet area) holds every animal that is no herd's, sized from the ceilings of the other races. A race is a herd at a fertile breeding pair or `herdMinAnimals` (5) animals, provided it has a policy ceiling (a founder or companion has none and stays misc); it gets a unit of its own, sized from that ceiling and not its headcount, near the misc unit when a site fits. Units are joined by their shared walls and matched to herds by key: every reservation of a herd's unit, a second unit and a top-up included, carries the herd's race in `LayoutReservation.Herd` (saved with the layout plan); the misc unit's carry none. A plan saved before the key is matched once by order (misc first, then races by name) and keyed on its next top-up (deletion candidate: that fallback). Reservations never move or shrink: an outgrown unit gets another reservation of the same kind beside it, and a unit boxed in so that nothing fits beside it founds one whole second unit (barn and vet area, never a part) for the overflow once every primary unit stands. A second unit inherits its herd's key, so a repeat top-up is a no-op and a new herd never takes it. Each barn has its own vet area (`VetBeds`). A vet area stands against its barn (a door in the shared wall). Each barn carries two openings on its ring beside each other, its regular `Door` for colonists and the animal flap into the paddock (the catalog's buildable `Building_Door` that roamers can open, `RoomFurniture.AnimalFlap`; a catalog without one is an error), since the flap lets no colonist through. Assigning animals to units is not planned here.

An explicit feed resource, or observed feed used exclusively by animals, creates an
owned `MaintainResource` concern through the existing acquisition/production methods.
The stock target uses native per-item nutrition and demand from competing eligible
eaters. Missing suitable feed, production prerequisites, handlers, access or storage
remain visible blockers. Cancelling the herd stops its unissued feed work. Acquiring
stock does not establish that animals can reach or have consumed it.

These are current-condition projections. Future births, changing temperatures,
spoilage and native job selection require fresh review; a bounded acceptance run
does not establish indefinite herd sustainability.

## Acceptance

`husbandry/dispatch` proves each method's native write on a fixture colony.
`husbandry/plan` proves the herd plan end to end on the lab: the race
catalog read (cows with a milk product, wild and tame races, minimum handling
skill); a lone cow founder kept beside an old milk race; the controller-built
vet room (its planned ring is staged finished from `LayoutPlan.VetRoomCells`;
beds, the medical flag, the `VetRoom` area and the sterilize writes are the
controller's) and an old male read back sterilized; a wild bull tamed as the
cow's mate, after which the old race retires one animal per review. Snapshot
tests own the planner decisions; the case owns native bed and room use,
surgery and the tame and designation ops. Its scope lists the native
semantics still unconfirmed (wildness stat, combat power and trainables
approximation, animal bed medical flag, surgery in the vet bed, bedroom role
with a sleeping spot, the `VetRoom` area, native master ids equal pawn ids);
a failure there names the one it hit.

## Food channel planning

The tick food plan aggregates native milk and egg production by race. Rates use
native resource nutrition, comp intervals and body-resource growth speed;
inactive producers contribute nothing, and missing rate, feed or reachability
facts remain unknown with an explanation. A race's rate is net of its herd's
feed: product minus the animals' feed per day (`UpkeepAnimal.Herd.FeedPerDay`),
never below zero, so a race that eats more than it yields earns no credit and
no herd floor. Milk costs 400 native gather work units per
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

`MaintainResource` compares the seasonal harvest gap with each enclosed pen's
native worst-quadrum pasture rate and stored feed. A negative balance can add a
`Plant_Haygrass` field through the existing soil planner, zone preview and Hands
admission. Existing hay-field capacity offsets new planting; a short season or
unknown capacity refuses that method. Hay stays human-inedible and out of human
food channels and human runway.

While the food runway is below target (a food-plan gap), eligible animals
above max(floor, breeding pair) rank by native meat nutrition per daily
grazing demand, then shorter reproduction interval, then animal ID. Missing
cost facts exclude the candidate. The plan sees one offered animal as a
one-shot `slaughter:` candidate of kind `Slaughter` (stock cap its meat
nutrition, lead 0, 180 native slaughter ticks upfront, no steady work), which
the floor and breeding-pair protection above leave to policy;
`MaintainHerd` dispatches it through its normal husbandry action.
