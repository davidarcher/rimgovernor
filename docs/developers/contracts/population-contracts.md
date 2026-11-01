# Population commitments

[Documentation](../../README.md)

The population target is the bot's own: `domain.PopulationTarget` (100), with no player knob. The
colony grows toward it only as fast as `policy.JoinerCapacity` allows: a food runway at or above
`policy.JoinerFoodFloorDays`, which rises linearly from 3 days for one hosted person to 15 days at
20 or more. No spare bed is required (#3749): a joiner sleeps on a `shelter/sleeping` stand-in
spot, and the new population changes the layout key, so the next replan adds their bed.

The population concern is `Population-<pawn ID>` per observed candidate, run as ordinary Hands actions.

- Admitted population is living free player colonists. Guests, prisoners and accepted candidates
  consume reserved capacity but stay distinct from admitted colonists. Shared food and shelter methods
  can provision future capacity.
- Custody orders require the food floor and available assigned doctors and wardens, and no spare
  bed (#3749). A capture goes to the roster's [warden](work-assignment.md#situational-roles) while one is
  available, otherwise the first available colonist by ID. Native capture/rescue previews separately
  require an eligible worker, reachable target and suitable available custody bed.

## Accommodation of hosted people

Every living person the colony hosts owns a slot of its own, and no pawn is counted into a bed it may not
sleep in. The habitat requirement (RG-HABITAT-001) is met per occupant, not by a colonist head count:

- **Colonists, guests and slaves.** `SleepingObservation` (the `shelter/sleeping` census) holds `People` (the
  colonists), `Slaves` and `Guests`; `Hosted` is the slaves and guests, `HostedSleepers` is `Hosted` less its babies and
  less the secure slaves the colonist count already holds (`CensusSlaves`, taken out of `People` by the bridge so
  each sleeper counts once), and `HostedBabies` is the babies among them. A hosted guest with no suitable bed owned
  and used stays a sleeping target, so `shelter/sleeping` stays unmet and builds the guest a bed through
  `SleepingMethod`; only its observed use of an owned suitable bed meets the Need, the proof colonists need.
- **Compatible slot.** `SleepingRules.CompatibleSlot` is the one test of a legal, reachable slot: a regular
  humanlike bed (not a crib, medical bed or prison bed) whose slave setting matches the pawn's (a slave only in a
  slave bed, no colonist or guest in one) and that the pawn can reach. It and `SleepingRules.CareSlot` share one
  class rule, `SleepingRules.LegalBed` (an animal only an animal bed, a prisoner only a prison bed, a slave only a
  slave bed that is no prison bed, a colonist or guest only a bed for neither). `SleepingRules.SlotShortfall` counts the
  sleepers short of such a slot per class; the sleeping review, the slave-bed mark, foothold sleeping, the stand-in count
  (`StandInsOwed`) and the care capacity comparison's conversions all use it, so a slave bed is never a
  colonist's spare and a conversion never takes a bed another sleeper needs.
- **Medical beds and hosted patients.** A medical bed is no free slot, but a pawn that owns no regular slot and lies in a
  legal medical bed, or seeks medical rest (`ShouldSeekMedicalRest`) and can reach one with a slot nobody lies in, is
  held by it (`SleepingRules.HeldInMedicalBeds`): it counts once, so a patient whose own bed was converted is not also
  short of a slot, and after care it wants a sleeping slot again, which the post-care review prices. A bed a care
  transport holds is its patient's slot ([medical care](medical-care.md#care-capacity)). Guests and slaves hosted
  by the colony are care patients in colony beds as their class allows; prisoners lie only in prison beds and animals
  only in animal beds, so none of those can be moved into a colony hospital bed.
- **Babies** are crib demand, not sleeping targets: the nursery sizes one crib per baby (a goal-named nursery with
  no baby yet still holds `MinNurseryBeds`), and the hosted slaves' and guests' babies join the station's crib demand
  (`StationFacts.HostedBabies`, through `ChildRoomNeeds(hostedBabies)`). A baby's own bed once it leaves the crib is
  a dated expected change (`UpkeepPerson.LeavesCrib`); when the comparison prepares it, the baby sizes a regular bed
  beside its crib (`StationFacts.CribLeavers`). A birth's crib and maternal bed rest stay unassessed.
- **Dated arrivals.** The lodgers an accepted (Ongoing) hosting quest of a classified people family names in its
  active `HostLodgers` objective that no census holds yet (joined by pawn id) are expected changes with a dated
  `QuestObjective.Arrival` and, for a stay that is not for good, a dated `Departure`; an undated arrival or departure is
  the named `Unread` fact (`arrival tick:<quest>`, `departure tick:<quest>`) and defers. The
  [prospective comparison](forecast-contracts.md#prospective-capacity) prepares a bed for them only when an assessed
  shortage loss exceeds its cost, and only a prepared change whose start is due joins the station's sizing: regular-bed
  lodgers its hosted count, prisoners its prisoner count. A quest not yet accepted, cancelled or finished brings
  nobody. The hospitality gate admits a quest only while vacant compatible reachable slots (`VacantArrivalSlots`, a
  colonist's reach standing for the arrival's) cover every lodger.
- **Prisoners.** `prisoners/recruit` is unmet while a living held prisoner owns no bed (`PrisonersFamily.Unbedded`),
  whatever its recruitment value, so the jail is staged for it; a prisoner can own only a bed set for prisoners.
  The jail marking never takes a bed its occupant owns (`JailRules.NextJailStep`). The Prison room credit prices the
  accommodation separately from recruitment ([forecast contracts](forecast-contracts.md#prison-accommodation-credit)).

Not modelled: no human pregnancy fact is read, so an expected birth is not crib demand before the baby exists; hosted
babies are not in `ChildRoomNeeds` (it counts colonist babies only); a prisoner outside a cell is not fed by wardens and
its hunger course is not priced (`prisoner_unfed_outside_cell_not_modelled`).

## Prisoner read and interactions

`observations_read_population` reads human pawn custody, recruitment eligibility, current
exclusive interaction, resistance, time held (`prisoner_ticks`, the `TimeAsPrisoner` record), food need,
bed and owned bed. It lists the exclusive interactions `PrisonerInteractionIntent` accepts:
`AttemptRecruit`, `MaintainOnly`, `ReduceResistance`, `Release`, and `Enslave` and `Convert` while
Ideology is active. Execution and non-exclusive toggles are player-only.

- The intent rides Actions/Apply. Native requires a living current-map colony prisoner when it
  applies; its gates (recruitable, wild man, classic ideology mode) refuse ineligible modes; a prisoner
  already set to the mode applies again.
- The population section carries recruitment eligibility, ideoligion and the wild-man flag. Biography
  (skills, passions, traits, incapable work types and age), summary health, luciferium addiction and worn
  apparel come from the same frame's canonical `PawnState`, joined by pawn ID. A missing pawn fails the
  read. Prisoner, custody and guest-care policy views retain their native person, pawn and typed custody
  rows; skills retain the native skill records, while traits add only catalog-derived worth. A trait
  without its definition catalog fails the read. The colony's best skills and living colonist count
  remain derived aggregates. The population snapshot also names the colony ideology mode. Slavery reads the ideoligion
  through the precept rule (`PrisonerColony.Ideology`, `EnslavedPrisoner`).
- **Organ harvest.** Each colony prisoner's joined pawn carries `surgery` (`missing_parts`, `operations` with a
  harvest's `yield_market_value`, and `surgery_bills`). The population custody row carries `faction_id`,
  and `harvest_goodwill_change`: the goodwill change vanilla's harvest violation report (-70) would
  make with its home faction after `CalculateAdjustedGoodwillChange` and the -100 floor, 0 when that
  goodwill cannot change. The snapshot carries nothing precept-shaped: `HarvestMood` reads the rule for
  `HarvestedOrgan` (and `SoldOrgan` when selling); without Ideology it is Core's thought.
- **Medical care.** Each colony prisoner's joined pawn settings carry `medical_care`; an operation reads
  `medicine_care_limited` when medicine it takes is stocked but that care level forbids it. Prisoners
  stay at herbal care at most: the `medical/surgery` Need pins a prisoner above `HerbalOrWorse` back to it through
  a care-only `WorkSettingsIntent` (native accepts it on a living colony prisoner) and never raises it.
  A harvest or part recovery blocked only by the limit is logged as refused and adds a `MedicineHerbal`
  want to economy/resource's targets.
- **Legless prisoners.** A prisoner missing both legs is never released: vanilla cannot release a downed
  pawn. Nothing takes or restores a prisoner's legs to control or train
  ([capability maintenance](capability-maintenance.md#no-practice-surgery-3344)).

### Prisoner use (`prisoners/recruit`, `policy.prisonerUse`)

The routine chooses each prisoner's use itself, with no player-only exemption:

1. Worth recruiting (`RecruitWorth` against the colonists' best skills, at least `RecruitThreshold` for
   the colony size): converted first while Ideology is active outside classic mode and it holds another
   ideoligion, then recruited.
2. Otherwise, able to labor and not a wild man: enslaved when the colony's slavery precept is
   `Slavery_Acceptable` or `Slavery_Honorable` (every other precept costs mood). `shelter/sleeping` gives
   each slave a bed set for slaves (`BuildingPatchIntent.for_slaves`).
3. Otherwise released: at once while the food runway is below `PlannerSettings.FoodTargetDays`, else after
   `PlannerSettings.PrisonerReleaseAfterDays` (15 by default) in custody.

A prisoner being recruited with its resistance broken keeps recruiting; an unknown fact never
authorizes a write. Native faction admission, resistance and recruitment probability are never written.
Each prisoner owed a change is its own `prisoners/recruit` Method (`prisoner-<mode>-<pawn>`, claiming
`prisoner/<pawn>`), so several prisoners' changes are in flight at once. A downed humanlike owed a
rescue or capture is the `prisoners/custody` Need; both remain held (see
[control loop](../architecture/control-loop.md#migration)).

## Joiners

The `population-joiner` routine family (on in the autonomous default; selected by
`RIMGOVERNOR_ROUTINE_FAMILIES` like every family) runs the `joiners` family (#3302), whose
`joiners/offers` and `joiners/letters` Needs answer joiner quests and letters from population capacity. The shared gate: living admitted colonists plus guests and prisoners are below
the population target and the food runway is at or above the food floor (`policy.JoinerCapacity`). No
spare bed is read or required (#3749): the joiner is a hosted sleeper at once, so `shelter/sleeping` stands in a
spot for it and the layout builds its bed ([accommodation](#accommodation-of-hosted-people)). Without a known food runway the offer
is left to expire through its own native timeout; nothing is rejected natively. Expired, changed and unsupported offers
are never answered.

- **Quests.** The rounds read the visible quest census and accept a not-yet-accepted
  classified joiner offer (a refugee chased by a threat; native checks `CanAcceptQuest` when it
  applies the `AcceptQuestIntent`). A reward-choice joiner offer takes the game's first option. Other quest offers share one family-table planner: allowed title claims take precedence, feasibility reserves spare capacity against open non-automatic quests, and unsupported objectives remain refused until their driver is available. An eligible required accepter is selected from native facts; refusal reasons are recorded once per quest and reason.
- **Letters.** Pending current-map `WandererJoins` letters are read through the typed colony census
  (`joiner_letters`) with letter ID, pawn, expiry and snapshot token. One letter answer is admitted at
  a time through `DialogIntent.joiner_letter_token` on Actions/Apply. Native rechecks the exact letter,
  quest, pawn, map, expiry and option under authority and runs its ordinary Accept option. It applies
  only once the offered pawn is a living spawned free colonist on that map; closing the letter alone is
  insufficient, and a resent intent for an already accepted letter applies again.
- **Creepjoiner letters** (`ChoiceLetter_AcceptCreepJoiner`) are letter rows with `creepjoiner` true
  under the same gate. The token prefix routes native to the letter's accept signal rather than an
  option, and the intent applies only once the pawn is a living free colonist.

### Creepjoiners

A creepjoiner is accepted, then held back from arms: its downside is hidden at arrival, so until it
shows the colonist is a risk and holds no weapon. The downside has shown when the tracker's
`downside_triggered` is true, or the colonist carries a trait or visible hediff that some downside def
in the definition catalog adds (`policy.CreepJoinerDownsides`, built from the catalog's downside rows;
no def name is written in the brain, and the colonist's own downside def is never read). Downside
checks read the canonical census's native `PawnState` directly: traits and visible hediffs are
predicates over its biography and health blocks. `CreepJoinerHand` retains that same pawn row;
identity, availability, primary weapon, allowed area, hunger, surgery operations and queued bills
are derived from it. The independent downside catalog is shared across those reads. Inactive
Anomaly means no creepjoiner. Missing required facts fail the read: a living pawn must have surgery
facts and an armed pawn must have its primary ID. A dead pawn may have no surgery block and
offers no operations. The tracker's `joined_tick` (`Pawn_CreepJoinerTracker.joinedTick`, #3844) dates the downside's timers for the
isolation room credit ([forecast value](../architecture/forecast-value.md)) and the harm term's downside part, which also reads its
`combat_power` (`kindDef.combatPower`, the aggressive downside's fight, #3844).

- **`admin/creep-joiners`** (Wellbeing Need of the colony admin family, `policy.AdminFamily`, #3264):
  each family run reads the owned census's colonist rows (complete roster only) and the stored record,
  and the Need is unmet while a weapon drop, an inspection, an isolation move or a disarm order is owed,
  or the record changed; unknown while the roster or a creepjoiner's downside cannot be read. An
  available colonist held back from arms that holds a weapon (`policy.CreepJoinerDownsides.WeaponDrops`)
  is not dropped yet: the Need is held as `unsupported_forecast_precautionary_disarm` until a drop is a
  shared forecast outcome. Every other order is its own Need-owned Method
  (`creepjoiner-{isolate|release|inspect|disarm}-<pawn>`), admitted by claims: independent pawns run side
  by side, the same order continues its open Method, and an overlapping claim is refused.
- **Arms consumers.** The equipment planner, the armory's weapon demand and the fight loadout set
  `EquipCandidatePawn.NoArms` from the same check ([weapon planner](weapon-planner.md)); the
  basic-defense unarmed count skips the colonist so `military/defense` does not stay unmet for it.
- **Surgical inspection** ([wiki](https://rimworldwiki.com/wiki/Doctoring#Surgical_inspection)): it
  reveals crumbling mind, organ decay and psychic agony early (their hediffs become visible) and
  nothing else. The game keeps no "inspected" flag; a clean one leaves a letter, an Anesthetic hediff
  (60000 ticks) and a small surgical cut.
  - A creepjoiner whose downside is unrevealed and who has no entry in the colony's record is queued
    the inspection by the same Need through the medical `ProductionBillIntent` (patient, recipe,
    `part_index`). The recipe is the catalog's `RecipeDef` whose `workerClass` is
    `Recipe_SurgicalInspection`, on the operation the pawn's native surgery read offers (lowest part
    index), with an eligible doctor and its ingredients on the map. A creepjoiner that cannot be
    ordered one stays unordered and the reason is logged.
  - The record is the `family/admin/creep_joiners` family record (`Store.CreepJoinersRecord`, the
    `GovernorState` blob, [persistence](persistence-contracts.md)), read back after a reload of the same
    colony and map: `ordered` when the inspection Method is admitted (one transaction), `done` once the pawn has no inspection bill queued. A bill the player removes also
    reads as done: the colony cannot tell a finished bill from a removed one. Pawns that left the colony
    drop out. An inspection that reveals nothing leaves the downside unrevealed.
- **Isolation.** A creepjoiner held apart (downside known hidden and no finished inspection in the
  record, `policy.CreepJoinerDownsides.Isolated`) owes one isolation room: a planned child room
  (`policy.IsolationRoomNeed`, role `RoomRoleIsolationRoom`, one bed) whose bed is the first
  `bed_humanlike` def the furniture rules resolve. The `shelter/safe-area` Need plans a bot-owned `Isolation`
  allowed area over its interior. Once the room stands the `admin/creep-joiners` Need moves the pawn
  into the area (an area-only `WorkAssignment`) and releases it, by clearing the
  area, when its downside shows, its inspection is done or it is hungry: the game treats food outside
  an allowed area as forbidden with no starvation exemption (`ForbidUtility.InAllowedArea`), so a
  hungry pawn is not kept in. The `Safe` area excludes the room. Known edges: a threat's shelter move
  may shuffle the pawn, another colonist may take the bed, and the room needs the `sheltering`
  toggle (`shelter/safe-area`) enabled.
- **Disarming an arrested creepjoiner** (dentures and wooden hands, then remove them so it can only
  headbutt). Game rules: a natural Hand or Jaw is offered no removal (`Recipe_RemoveBodyPart` offers a
  part with an added part, a clean one whose def spawns a thing on removal, one with a bad visible
  condition, or `forceAlwaysRemovable`; neither def does); `Recipe_InstallArtificialBodyPart` installs
  over it with no violation on a pawn of the doer's faction or of none, and the added part makes the
  part removable; removing it leaves the part missing.
  - Sites come from the definition catalog: the race's melee tools other than the
    `ensureLinkedBodyPartsGroupAlwaysUsable` one (the head) each link a body part group, and the site is
    the lowest part holding every part of that group in `BodyDef` pre-order index
    (`bridge.CreepJoinerDisarm`, `policy.CreepJoinerDisarm`).
  - For each colony prisoner with a creepjoiner tracker (`PrisonerFacts.CreepJoiner`, `Kind`),
    `admin/creep-joiners` queues, one bill at a time, the removal of an installed part first, else the
    cheapest install the game offers on a site that is no violation, with an eligible doctor and
    stocked ingredients. Price is the items the recipe names singly (`IngredientCount.count` at market
    value) plus the operation's medicine value; a recipe with no known price is refused with a log line.
  - A site is done when its part or an ancestor is missing, so the work carries no record. A recruited
    prisoner is a colonist again and normal surgery care applies.

Open: valuable apparel has no expressible rule yet. A downside trait or hediff that the
colonist's benefit or form also grants counts as shown (the catalog rows do not tell the two apart).
The drop job, inspection and disarm are unverified in game (no acceptance run).

## Prisoner effect observation

Progress observation reads the pawn's actual custody state, not only the setting.
`PrisonerEffect.outcome` is `held`, `recruited`, `enslaved`, `converted`, `released`, `escaped` or
`died`. While held, the order is complete as long as its setting is still set. Once the pawn leaves
custody, the order is complete only when the outcome is the one its mode pursues
(recruit→recruited, release→released, enslave→enslaved, convert→converted) and unsuccessful
otherwise. A pawn no longer observable anywhere stays unknown.

## Capture and rescue

- A `GiveJobIntent` Capture uses the installed game's capture eligibility, manipulation, reservation
  and bed checks, then its ordinary Capture job. Non-hostile capture is unsupported because it changes
  faction relations.
- A Capture whose patient has `CompHoldingPlatformTarget.StudiedAtHoldingPlatform` true is the game's
  holding-platform order: native sets the entity's `targetHolder` to the available platform of highest
  containment strength and gives the carrier `CarryToEntityHolder`. Ordinary humans can also carry this
  component; its presence alone never selects entity capture. The deciding rule is in
  [facilities](../architecture/facilities.md).
- Rescue is the ordinary rescue path. Unknown prerequisites block new commitments.
- A care rescue names its exact bed: `Rescue(patient, bed)` carries the patient to the bed the care
  comparison chose, native checks `RestUtility.IsValidBedFor` for that patient and rescuer instead of searching, and
  the bed is claimed until the patient lies in it ([medical care](medical-care.md#care-capacity)). Capture and the
  combat rescue keep their own bed searches. A walking guest, prisoner or animal cannot be ordered to lie down, so it
  is not moved by a bed-rest order; only a colonist or slave the player controls is.
- Surgery legality for hosted and held pawns follows the bed: a surgery on a colony pawn is rated in the bed the
  patient is operated in (`RestUtility.FindPatientBedFor`); `SurgeryChance.NoCareBed` is the absence of one, and the
  forecast's hospital credit counts a colonist's operation only, naming `hospital_guest_surgery_unmodelled` for
  guests (the one non-colonist class a colony hospital bed takes).

## Completion and integration

An issued order is not successful custody. The concern observes actual prisoner status and bed occupancy;
care requires observed food and tending state; recruitment requires a native free-colonist read.
Integration requires an owned indoor non-prisoner bed, active work allocation, equipment (or native
incapability of violence) and current care needs met. Shared work/shelter methods handle new colonists;
the population method can allocate an available eligible weapon without replacing existing gear
(weapon groups containing forbidden instances are skipped because position samples do not expose
individual forbidden state). Completed admission never authorizes recapturing a pawn who later leaves.

Pending orders keep the normal identity, direction, uncertainty and cancellation guards. Population
methods do not replay custody orders automatically. A ten-day native-tick watchdog bounds stalled
issued work. Patient-care, recruitment and integration are separate observed phases; voluntary joining
after rescue remains RimWorld's decision.

The optional `PopulationFixture` build prepares test-only starting conditions and is excluded from
production and model execution. Acceptance evidence distinguishes its prerequisites from the ordinary
pawn outcomes asserted afterward. Its custody progress probes treat an absent spawned-pawn row
as unobserved while a carrier may hold the patient, and continue only within the case tick budget.
Capture requires the patient to reappear as a prisoner in bed; rescue requires observed bed occupancy.
