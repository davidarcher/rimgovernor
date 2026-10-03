# Population commitments

[Documentation](../../README.md)

The population target is the bot's own: `domain.PopulationTarget` (100), with no
player knob (#1032). The colony grows toward it only as fast as
`policy.JoinerCapacity` allows: a spare colonist bed and a food runway at or above
`policy.JoinerFoodFloorDays`, which rises linearly from 3 days for one hosted
person to 15 days at 20 or more.
`SetPopulationDecision` records an exact observed pawn ID and one of `rescue`,
`capture`, `recruit` or `ignore`. Ignore cancels future population work; it does not
release a prisoner or undo a native order.

Each decision uses a `Population-<pawn ID>` ColonyGoal and ordinary Hands actions.
The controller counts living free player colonists as admitted population. Guests,
prisoners and accepted candidates consume reserved capacity but remain distinct from
admitted colonists. Shared food and shelter methods can provision future capacity.
Custody orders require the food floor, spare colonist beds and available
assigned doctors and wardens. A capture goes to the roster's
[warden](work-assignment.md#situational-roles) while it is available, otherwise
the first available colonist by ID. Native capture/rescue previews separately
require an eligible worker, reachable target and suitable available custody bed.

`rimgovernor/observations_read_population` reads human pawn custody, recruitment
eligibility, current exclusive interaction, resistance, time held as a prisoner
(`prisoner_ticks`, the `TimeAsPrisoner` record), food need, bed and owned bed,
and lists the installed exclusive interactions `PrisonerInteractionIntent` accepts:
`AttemptRecruit`, `MaintainOnly`, `ReduceResistance`, `Release`, and `Enslave` and
`Convert` while Ideology is active. Execution and non-exclusive toggles are player-only.
The intent rides Actions/Apply: native requires a living current-map colony prisoner
when it applies, its gates (recruitable, wild man, classic ideology mode) refuse
ineligible modes, and a prisoner already set to the mode applies again. Routine planning (`MaintainPopulation`) chooses each prisoner's use itself, with no
player-only exemption (`policy.prisonerUse`). The read also carries each prisoner's
will, ideoligion, wild-man flag, biography (skills, passions, traits, incapable work
types, age) and summary health, the free colonists' biographies, and the snapshot's
`ideology_active`, `classic_ideo_mode` and `colony_ideo_id`; slavery reads the ideoligion through the precept rule (`PrisonerColony.Ideology`, `EnslavedPrisoner`).
For organ harvest (#1169) each colony prisoner also carries `surgery` (its
`missing_parts`, `operations`, with a harvest's `yield_market_value`, and
`surgery_bills`, from the care read's producer), `faction_id`, and
`harvest_goodwill_change`: the goodwill change vanilla's harvest violation report
(-70) would make with its home faction after `CalculateAdjustedGoodwillChange` and
the -100 floor, 0 when that goodwill cannot change. The snapshot carries
nothing precept-shaped: `HarvestMood` reads the rule for `HarvestedOrgan` (and `SoldOrgan` when selling); without Ideology it is Core's thought.
Each colony prisoner also carries `medical_care` (#1239), and an operation reads
`medicine_care_limited` when medicine it takes is stocked but that care level
forbids it. Prisoners stay at herbal care at most: MaintainSurgery pins a prisoner
above `HerbalOrWorse` back to it through a care-only `WorkSettingsIntent` (which
native accepts on a living colony prisoner) and never raises it. A harvest or part
recovery blocked only by the limit is logged as refused and adds a
`MedicineHerbal` want to MaintainResource's targets.
For peg-leg control (#1236) a colony prisoner carries
`withdrawal`: it has a drug addiction, which a prisoner cannot feed. A legless
prisoner is never released until MaintainSurgery puts a peg leg back.
A recruitable prisoner worth recruiting (`RecruitWorth` against the colonists' best
skills, at least `RecruitThreshold` for the colony size) is converted first while
Ideology is active outside classic mode and it holds another ideoligion, then
recruited. Otherwise one able to labor, not a wild man, is enslaved when the
colony's slavery precept is `Slavery_Acceptable` or `Slavery_Honorable` (every
other precept costs mood). Otherwise it is released: at once while the food runway
is below `RoutinePolicy.FoodTargetDays`, else after
`RoutinePolicy.PrisonerReleaseAfterDays` (15 by default) in custody. A prisoner
already being recruited with its resistance broken keeps recruiting, and an unknown
fact never authorizes a write. MaintainHousing gives each slave a bed set for
slaves (`BuildingPatchIntent.for_slaves`). Native faction admission, resistance and recruitment
probability are never written.

The `population-joiner` routine family (on in the autonomous default; selected by
`RIMGOVERNOR_ROUTINE_FAMILIES` like every other family) lets the same
goal answer joiner quests from population capacity. The routine review reads the
visible quest census and accepts a not-yet-accepted `ThreatReward_*_Joiner` offer
(a refugee chased by a threat; native checks `CanAcceptQuest` when it applies
the `AcceptQuestIntent`) only when living admitted colonists plus guests and prisoners are below the
population target, the food runway is at or above the food floor and an unowned humanlike, non-medical,
non-prisoner bed reads back. Without room, the offer is left
to expire; nothing is ever rejected natively, and a reward-choice offer takes the
game's first option. Pending current-map `WandererJoins` letters are read through
the typed colony census (`joiner_letters`) with their letter ID, pawn, expiry
and snapshot token. The same capacity gate admits one letter answer at a time
through `DialogIntent.joiner_letter_token` on Actions/Apply. Native rechecks the exact letter,
quest, pawn, map, expiry and option under authority and runs its ordinary Accept
option. The intent applies only once the offered pawn is a living spawned free colonist
on that map; closing the letter alone is insufficient, and a resent intent for a
letter already accepted that way applies again. A creepjoiner offer
(`ChoiceLetter_AcceptCreepJoiner`, #1740; David 2026-10-03: accept creepjoiners)
is a letter row with `creepjoiner` true under the same gate. Its token prefix routes native to the
letter's accept signal rather than an option, and the intent applies only once the
pawn is a living free colonist. Expired, changed and
unsupported offers are never answered. Without known capacity, letters expire
through their own native quest timeout.

### Creepjoiners

A creepjoiner's downside is hidden at arrival, so until it shows the colonist
is a risk and holds no weapon (#1740, David 2026-10-03: accept first, give no
gear). The downside has shown when the tracker's `downside_triggered` is true,
or the colonist carries a trait or a visible hediff that some downside def in
the definition catalog adds (`policy.CreepJoinerDownsides`, built from the
catalog's downside rows; no def name is written in Go, and the colonist's own
downside def is never read). Facts the answer rests on that are unread leave
it unknown, and an unknown holds the colonist back; a colonist with no
Anomaly block is no creepjoiner.

- `ManageCreepJoiners` (People, priority 3, `policy.CreepJoinerDownsides.WeaponDrops`):
  each review reads the frame's colonist rows (complete roster only) and opens
  the goal while an available colonist held back from arms holds a weapon. Its
  planner orders one `drop_equipment` action per such colonist in one plan
  (job token `DropWeapon`, [action contracts](action-contracts.md)); a drop is
  development-exempt like an `Equip`. The goal recovers when the weapon is out
  of the colonist's hands; the dropped weapon is then loose stock.
- The equipment planner, the armory's weapon demand and the fight loadout set
  `EquipCandidatePawn.NoArms` from the same check
  ([weapon planner](weapon-planner.md)); the basic-defense unarmed count skips
  the colonist so `EnsureBasicDefense` does not stay open for it.
- **Surgical inspection** (David 2026-10-03; the wiki's
  [Doctoring, Surgical inspection](https://rimworldwiki.com/wiki/Doctoring#Surgical_inspection)):
  an inspection reveals crumbling mind, organ decay and psychic agony early
  (their hediffs become visible) and nothing else, and the game keeps no
  "inspected" flag on the pawn: a clean one leaves a letter, an Anesthetic
  hediff (60000 ticks) and a small surgical cut. A creepjoiner whose downside
  is unrevealed and who has no entry in the colony's own record is queued the
  inspection through the existing medical `ProductionBillIntent` (patient,
  recipe, `part_index`) by the same goal: the recipe is the def mirror's
  `RecipeDef` whose `workerClass` is `Recipe_SurgicalInspection` (no def name
  in Go), on the operation the pawn's native surgery read offers (lowest part
  index) with an eligible doctor and its ingredients on the map; a creepjoiner
  that cannot be ordered one stays unordered and the reason is logged. The
  record is `policy.CreepJoinerRecord` in the goal's `Record` (the
  `GovernorState` goal blob, [persistence](persistence-contracts.md)): `ordered`
  when the bill is queued with the plan (one transaction), `done` once the pawn
  has no inspection bill queued. A bill the player removes also reads as done:
  the colony cannot tell a finished bill from a removed one. Pawns that left the
  colony drop out of the record. An inspection that reveals nothing leaves the
  downside unrevealed.

Open: the isolation room role and the prosthetic trick (dentures and wooden
hands, then removal); valuable apparel has no expressible rule yet. A downside
trait or hediff that the colonist's benefit or form also grants counts as shown
(the catalog rows do not tell the two apart): unverified in game, as the drop
job's and the inspection's build and admission are (no acceptance run).

Progress observation of a prisoner order reads the pawn's actual custody state, not
only the setting: `PrisonerEffect.outcome` is `held`, `recruited`, `enslaved`,
`converted`, `released`, `escaped` or `died`. While held, the order is complete as
long as its setting is still set. Once the pawn leaves custody, the order is complete
only when the outcome is the one its mode pursues (recruit→recruited,
release→released, enslave→enslaved, convert→converted) and unsuccessful otherwise;
a pawn that is no longer observable anywhere stays unknown.

A `GiveJobIntent` Capture uses the installed game's capture eligibility, manipulation,
reservation and bed checks, followed by its ordinary Capture job. Non-hostile capture
is explicitly unsupported because it changes faction relations. Standing neutral shrine ancients use the existing `Arrest` operation through the Capture action instead: the journal's `OccupantCapture` decision and known JoinerCapacity create a MaintainPopulation deficit. Its custody planner runs before routine work, wakes after casket opening, reserves an exact vacant prisoner bed and couples arrest to a plan-owned draft (#939): the open plan keeps the arrester out of the undraft sweep. Native completion requires living custody in that bed; no second Arrest order kind is defined. Rescue remains the
existing ordinary rescue path. Unknown prerequisites block new commitments.

An issued order is not successful custody. The population goal observes actual
prisoner status and bed occupancy; care requires observed food and tending state.
Recruitment requires a native free-colonist read. Integration requires an owned indoor non-prisoner bed,
active work allocation, equipment (or native incapability of violence), and current
care needs met. Shared work/shelter methods handle new colonists, and the population
method can allocate an available eligible weapon without replacing existing gear. Weapon groups containing forbidden instances are
skipped because the position samples do not expose individual forbidden state.
Completed admission never authorizes recapturing a pawn who later leaves.

Pending orders retain normal identity, direction, uncertainty and cancellation guards.
Population methods do not automatically replay custody orders. A ten-day native-tick
watchdog bounds stalled issued work. Patient-care, recruitment and integration are
separate observed phases; voluntary joining after rescue remains RimWorld's decision.

The optional `PopulationFixture` build prepares test-only starting conditions and is
excluded from production and model execution. Acceptance evidence distinguishes this
fixture's prerequisites from the ordinary pawn outcomes asserted afterward.
