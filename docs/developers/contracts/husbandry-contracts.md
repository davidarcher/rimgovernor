# Animal husbandry contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`MaintainHerd` creates a persistent, player-owned `MaintainHerd-<race>` goal in
ColonyPlan. It sizes each race itself (#875); there are no operator flags.

- **Cap.** Every observed race is capped at the smaller of
  the wealth-budget cap (#1189) — 30 per race while `WealthBudget(raid
  points, defense capacity, wealth)` has non-negative headroom, else
  `clamp(floor(30 × (wealth + headroom) / wealth), 6, 30)`, i.e. 30 × defense
  capacity / raid points: 15 when capacity is half the raid points, 6 at a
  fifth or less — and, for pen animals, the pasture
  cap `floor(n × B / D)`: `B` is the pens' worst-quadrum pasture plus stored
  feed spread over 15 days, `D` their grazing demand, `n` the race's penned
  count. RimWorld's pen capacity is this same nutrition balance
  (`PenFoodCalculator`), so there is no separate density term. Predators are
  never penned; they eat meat and stay under the stored-food feed gate.
  An unknown budget (raid points, capacity or wealth) or pen facts leave
  that term out.
- **Floor.** The food plan's productive-animal floor (`FoodHerdPolicy`), and a
  breeding pair for any race producing milk, wool, chemfuel or eggs, clipped
  to the cap. Below it, the lowest-ID tameable wild animal of that race is
  designated (`tame`) while `MaintainAnimalFeed`'s review reports no shortfall
  and a [handler](work-assignment.md#situational-roles) (`TamerFor`) clears its
  `minimum_handling_skill`. Predators and races with
  `manhunterOnTameFailChance ≥ 0.2` are never tamed, except grizzly and polar
  bears and wargs.
- **Removal.** Above the cap, surplus goes by `slaughter` whenever native
  `SafeToSlaughter` allows it (not bonded, no master, not pregnant, not
  designated) and the player ideo neither venerates the race nor has an
  `AnimalSlaughter` precept; otherwise by `release` when `SafeToRelease`
  allows it (same exclusions). Cull order: old (past 80% of
  `lifeExpectancy`) or sick; males beyond one per five females; untrained
  adults by highest grazing demand per meat; adults that learned Haul, Rescue
  or Release (attack); juveniles only while pasture is short (`B < D`). Ties
  go to the lowest ID.
- **Breeding pair.** No removal leaves a race with fewer than one male and two
  females; an animal of unknown sex is never removed. Standing designations
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
| `release` | player animal | `ReleaseAnimalToWild` designation | designation present |
| `tame` | wild animal | `Tame` designation | designation present, or the animal now reads as a player animal (the taming job consumed it) |
| `allowed_area` | player animal | `AreaRestrictionInPawnCurrentMap` (argument: area id, empty clears) | animal's allowed area reads back equal |
| `master` | player animal | `PlayerSettings.Master` (argument: colonist id, empty clears) | master reads back equal |
| `follow_drafted` | player animal | `followDrafted` (argument: `true`/`false`) | flag reads back equal |
| `follow_fieldwork` | player animal | `followFieldwork` (argument: `true`/`false`) | flag reads back equal |

Apply-time rules: `allowed_area` needs `SupportsAllowedAreas`; `master` and the
follow flags need `Obedient` (learned Obedience; native refuses otherwise). An
area or master id the map does not carry is refused as not found. Each follow
method writes only its own flag.

Selection order each cycle is train, then tame, then surplus removal; one write
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
Removal-designated animals receive no training changes.

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
gets a derived population floor. The effective minimum is at least the operator
minimum; an explicit maximum bounds only the derived addition. The portfolio
explains `MaintainHerd-<race>` and its floor. Both herd review and dispatch use
that policy, including the existing feed gate before taming.

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

