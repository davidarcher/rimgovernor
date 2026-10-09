# Work assignment contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

The work planner (`policy.PlanWork`, `go/internal/policy/work_assignment.go`)
turns the routine read's pawns into one priority matrix per review: every work
type native reports, every available colonist whose work applies. It is a
proposal compared against the readback (`Matches`), never permission to change
a pawn's settings; the work review dispatches the differences as a
`WorkSettingsIntent` on Actions/Apply. The plan owns every
priority: no player edit is exempt, and a differing readback is replaced.

## Pawn profile

`policy.BuildProfile` reads a `PawnProfile` from the same `WorkPawn` the review
already carries (`observation/rounds_work.go`): skills with the effective
level, the stored level beneath aptitude, passion and disabled flag; traits with
degree; the backstory-incapable work types; biological age (`Child` from the Biotech developmental stage).
Unknown traits, incapable rows or age leave those parts empty and the planner
skill-only for that pawn; they never make the review unknown.

Trait effects are read from the TraitDef rows of the catalog at observation
time (`DefinitionCatalog.TraitEffects`) and carried on each
`PawnTrait.Effects`; a trait or degree the catalog lacks fails the read with a
named error. The rows give:

| Effect | Derived from |
| --- | --- |
| `WorkSpeed`, `LearnRate`, `MoveSpeed` | the summed WorkSpeedGlobal, GlobalLearningFactor and MoveSpeed stat offsets of the degree |
| `DisabledWork` | the work types whose work tags meet the trait's disabledWorkTags, plus its disabledWorkTypes (Pyromaniac: Firefighter) |
| `QuickSleeper` | a RestRateMultiplier offset above zero |
| `Undergrounder` | the Outdoors need among disablesNeeds |
| `Cannibal` | the human-meat ingestion thoughts among disallowedThoughtsFromIngestion |

The remaining flags (`GreatMemory`, `NightShift`, `MeleeOnly`, `FrontLine`,
`RearRanged`, `Pyromaniac`, `Sociable`, `Execution`, `SurgeonSafe`, `Nudist`,
`Ascetic`, `Gourmand`, `ChemicalInterest`, `Greedy`, `Jealous`) are policy
decisions with no rule over the rows; they stay in the small `traitFlags` table
in `pawn_profile.go`, keyed by TraitDef name and degree, and are merged onto the
derived effects. A trait with neither rows nor flags (a mod trait the catalog
does not carry fails the read; a mood/nerves spectrum native already folds into
break thresholds) contributes nothing beyond its rows. The gear
([equipment upkeep](equipment-upkeep.md)), drug and room concerns read their flags
from `PawnProfile.Effects`; mood control keeps its native thresholds.

`ProfileSkill.LearnFactor` is the passion multiplier (none 0.35, minor 1.0,
major 1.5) scaled by `1 + LearnRate`.

## Planner

Work types are filled in native natural-priority order (Doctor, Warden,
Handling, Cooking, Hunting, Construction, Growing, Mining, PlantCutting,
Smithing, Tailoring, Art, Crafting, Research, then any unlisted type by name).
For each:

- **Demand** is the owner count wanted: the review's `WorkRequirement`s (bench
  bills, blueprints' construction minimum, research), a baseline
  (`baselineDemand`: one doctor, one cook per eight pawns, one grower per 150
  field cells, one constructor — two with blueprints pending and six pawns —,
  one plant cutter, one hunter — two with four pawns —, a warden while a
  prisoner is held; `RoundsWorkDemand` reads the census from the routine facts)
  and, for any other skilled type, one owner when a natural specialist exists
  (level 6 or a passion). Handling also has one owner while the herd plan holds a
  milk or wool job (`WorkDemand.Handling`): gathering yield and speed scale
  with Animals.
- **Capable** is not incapable, not trait-forbidden (Pyromaniac Firefighter,
  Brawler Hunting, Abrasive Warden), Hunting only with a ranged primary
  (or, while the food plan has opened a hunt waiting on a hunter's weapon,
  `WorkDemand.Arming`, for the best unarmed Shooting colonists the baseline
  Hunting demand lacks an armed owner for, #2162), and
  the skill at or above the floor: Cooking 5 (food poisoning), Doctor and
  Construction 4, a requirement's minimum, otherwise 0. When nobody clears a
  safety floor for a demanded type, the best pawn the requirement admits owns
  it anyway and the coverage row reports `Capable: 0`.
- **Fitness** is level + passion (minor 2, major 4) + 10·`WorkSpeed` + role
  terms (hunter 5·`MoveSpeed`; warden 5·`Sociable`, +1 execution-safe; doctor
  +1 surgeon-safe) + 1.5 for the incumbent (priority 1 in the readback, the
  hysteresis that keeps the matrix stable across reviews) − 3 per work type
  already owned. Construction owners are ordered by raw level first; a hunter
  who already owns Growing or Cooking loses the field.
- **Owners** are the top `Demand` candidates at priority 1. **Secondaries**
  at 2 are the next-best backup when the type has demand and every passion
  within five levels of the weakest owner, so it trains beside the specialist.
  A skill above 10 that no owner or secondary slot exercises is flagged
  `Decaying`; a pawn owning nothing keeps it exercised at 2.
- **Everyone capable** takes 3 (level 8 or unskilled work) or 4 under manual
  priorities, enabled in checkbox mode. Firefighter, Patient, BedRest and
  Childcare stay 1; Hauling, Cleaning and BasicWorker 3, Hauling and Cleaning
  4 for the research owner.

`WorkDecision.Coverage` lists demand, owners and capable count per type
(`RoundsFacts.WorkRoster`). `Capacity` is false when a required type or a core
role (Doctor, Cooking, Construction, Growing) found no owner. `Matches`
compares the proposal to the readback with checkbox semantics when manual
priorities are off (enabled or not, never the rank).

## Ready work

`ProjectReadyWork` (`ready_work.go`) projects plans and proposals into diagnostic
candidates (`Rounds.ReadyWork`); admission does not read it. Parallelism is the
observed count of eligible workers or claims, with no constant cap. An action
kind without a stage adapter (everything but Building, Haul, CutPlant,
AreaPlantCut, ProductionBill, GrowerCrop, ZoneCreate, SupplyAllow and
SupplyForbid, and a bill whose recipe `RecipeFacts` does not know) reports
`awaiting` with reason `eligibility_unknown` and no work or parallelism; it is
never guessed from the goal's labor profile and adds no demand. Dependencies
and holds still read `blocked`.

## Construction helpers

Owners stay the skilled constructors; `WorkDemand.Help`
(`construction_helpers.go`) adds bounded help below the floor. Demand
is `ConstructionHelpDemand` over ready work, the open plans' building
definitions (with their observed `ConstructionSkill`) and the current native
construction census. There is no definition-name list: every building
definition in play (an observed site, an open plan, a construction candidate)
is classified from observed facts.

- Quality-free (`QualitySensitive` false) with an observed skill
  prerequisite (the plan definition's, else the site's native finishing
  skill) is helper work. The parallelism of its runnable `building:<def>`
  candidates is demand, and the highest such prerequisite is the helper floor.
- Quality-sensitive with an observed `MinimumFinishingSkill` is protected:
  native enforces the setting per target, so it neither counts as demand nor
  moves the floor.
- Anything else withholds helpers and withdraws current ones at once, under
  the reason naming the fact: `construction_prerequisite_unknown` (also a
  construction candidate with no `building:` definition),
  `construction_quality_unknown` (no observed site or unreadable quality fact)
  or `construction_quality_unprotected` (a quality site awaiting its finishing
  minimum from the quality adoption below). `Withheld` lists the definitions.

A material-filled observed frame is runnable even when its placement action
is already applied; blueprints and unknown or incomplete material delivery do
not contribute readiness. Unmet demand is that parallelism beyond the owners
not held by another work type's job. A helper is a pawn under the floor that
the helper floor admits, not resting, idle (`idleJob`) now and at the
previous review, or already helping; helpers take priority 4 (enabled in
checkbox mode), previous helpers first, then by level, at most one per unmet
task. No other work type's floor moves. Cheap-material cost is not modelled:
native refuses what a helper cannot build, and the frame cost is not an
observed per-definition fact.

A work-type priority enables every native construction job, not one wall.
Native still refuses a frame above the pawn's `constructionSkillPrerequisite`.
Unknown or other-world ready work with no census authorizes nothing
(`construction_demand_unknown`). A frame placed between two reviews follows
vanilla rules until Go adopts it.

## Quality construction

The Work planner adopts every observed colony-owned quality-sensitive blueprint
or frame, including player-placed sites. Native `CompQuality` classification
identifies these targets. `ConstructionSkillChoices` chooses the highest current
Construction level among capable colony builders once at first adoption. Busy,
asleep or drafted builders keep their capability; equal-skilled builders qualify.
Unknown capability or no builder meeting the inherent prerequisite leaves the
target unconfigured, withholding helpers (`construction_quality_unprotected`).

The existing `BuildingIntent` carries `minimum_finishing_skill`; adoption adds
`existing_target_id`, which refuses stale targets rather than placing replacements.
The setting is journaled before dispatch and observed in `ConstructionState`.
`ConstructionSkillState` saves its exact native thing reference (shared with the
[construction tier](construction-tiers.md)) and transfers it
only through the blueprint's own frame conversion. Cancellation/completion and a
new same-cell target never inherit a setting. Setting adoption confers no upkeep
ownership and consumes no pawn labor.

Vanilla finishing work selection and the completion backstop enforce the greater
of the configured minimum and the inherent skill prerequisite, including prioritized
orders; there is no bypass. Material hauling stays unrestricted. Unconfigured
sites retain vanilla behavior. A configured floor never ratchets upward or silently
lowers. A lost eligible builder produces target-local `no_qualified_builder`
readback and inspection text; other projects continue. Native case
`wall/construction-skill` covers conversion, save/load, safe-wall progress,
equal-skill completion, refused low-skill finishing and same-cell isolation.

When unmet demand clears, helpers hold for `ConstructionHelpHoldTicks`
(2500) from the last tick it held (`held_after_demand`), then return to the
ordinary 0; an override set meanwhile wins. The record (`Idle`, `Helpers`,
`DemandTick`, `Ready`, `Unmet`, `Reason`, `Withheld`) rides the roster report
(`Rounds.Roster.Help`), which the next review reads back for the
hysteresis; `no_sustained_idle_pawn` and `no_unmet_suitable_construction`
name why spare capacity went unused.

## Situational roles

`go/internal/policy/pawn_roles.go` answers the questions other concerns ask of the
roster from the same profiles, each a pure function returning the pawn and
whether one qualifies: `WardenFor(execution)`
(Social, Kind preferred, Abrasive never; Psychopath or Bloodlust for an
execution), `TraderFor` (Social, Abrasive only when nobody else can talk),
`TamerFor(minimum)` (Animals at the animal's `minimum_handling_skill`),
`HunterFor` (Shooting, ranged, never a Brawler, fast walkers preferred) and
`FrontLine` (Tough, Nimble, Brawler or Melee over Shooting hold the line; the
rest shoot behind it). The trade review opens with `TraderFor` among the
negotiators native lists as eligible (native's own first row when the roster
is unknown or nobody qualifies). The defense review splits its defenders with
`FrontLine` from the combat read's biography: line holders take a melee
opponent first, shooters a ranged opponent and the layout's firing cells
first, as a preference over the ID order that stands when the profile is
unknown. The custody review sends `WardenFor` to capture a downed hostile while
that pawn is available. The husbandry review tames only a wild animal `TamerFor`
finds a handler for at its minimum handling skill. The acquisition reviews
(food, wood, pests) spend the hunting budget only while `HunterFor` finds a
hunter on a known roster.

## Schedules

`policy.PlanSchedules` (`pawn_schedule.go`) gives each available pawn a
role-based timetable from the same profile: the native day sleeps 22h-5h; a NightOwl sleeps 10h-17h and is free overnight; a QuickSleeper's
Sleep block shrinks to six hours. Joy is the hour right before sleep and
every other hour is Anything. The planner never writes Work: Work blocks
ignore rest and recreation and wake sleeping pawns. Every
known timetable is planned, whoever wrote it: a timetable edited under Manual
is replanned like any other once Auto holds (control-loop.md, Manual control); an unknown timetable (issue `schedule`) is skipped.

`policy.PawnProfile.WorkHours` is the pawn's work hours per day: the count of
timetable hours assigned to `Work` or `Anything` (`WorkPawn.WorkHoursPerDay`,
the same count the quest capacity forecast uses). It adds no wire field; it is
derived from the `schedule` the work read already carries. An unread, non-24-hour
or unclassifiable timetable leaves it unknown, never a default. Nothing acts on
it yet; the production rate model (#2590) reads it as worker-hours.

The work review sends a mismatching timetable in the pawn's
`WorkSettingsIntent` (`domain.NewScheduleAssignment`, `Schedule.assignment_defs`
all 24 hours, `SettingsField.Schedule` in the applied result) together with the
priorities. Native (`WorkSettingsActionHandler`) requires every
`TimeAssignmentDef` and a 24-slot tracker when it applies, writes through
`Pawn_TimetableTracker.SetAssignment`, and reads the timetable back into
`Matches`. `RoundsFacts.WorkCoverage` is false while any planned timetable
differs from the readback.

## Mech control

`policy.PlanMechControl` (`mech_control.go`) puts each mechanitor's
mechs in role groups and sets each group's mode through the `mech_control_group`
and `mech_work_mode` arms of `PawnSettingsAction`: every move first, then each
group's mode (the mode is per group). A work mech (`MechKindRow.work_mech`) is a
worker, any other kind a guard. With two or more control groups (vanilla default
2) workers go to the group already holding most of them, guards to the busiest
other group; workers run `Work`, guards `Escort` (the wiki's "do available work
tasks" and "follow the mechanitor and fight enemies"; `MechWorkModeDefOf` also has
`SelfShutdown`, unused here). The modes are never named in Go: the catalog's
`MechWorkModeRow` flags `work`, `escort` and `recharge` mark `MechWorkModeDefOf.Work`,
`.Escort` and `.Recharge` (native reflection), each on exactly one row or the
catalog fails to decode. The plan fails without those modes or any mech's kind. A mech with no overseer among the
inputs, or an unread group, is left alone.

Colonist need versus bandwidth, decided from the read: control never changes
bandwidth, which only acquiring mechs spends (gestation). Colonist need
decides what the next free bandwidth buys (`MechRoleNext`): a worker while a
`WorkCoverage` has fewer owners than demand, no work mech of the mechanitor
covers that work type and a catalog work mech kind lists it; a guard otherwise;
nothing with no free bandwidth. With a single control group threat beats work:
`Escort` while a hostile is on the map or the group holds only guards, `Work`
otherwise.

Recharging (`mech_recharge.go`) rides the same group mode. Each mech row
carries `PawnMech.energy` (`Need_MechEnergy`, 0-1) and its control group's own
recharge band (`recharge_below`/`recharge_above`, the private
`MechanitorControlGroup.mechRechargeThresholds` the game recharges within), so
no threshold is a bot number. With a charger ready (`MechChargerReady`: powered,
not full of waste) a group with any mech under its band's lower bound runs the
catalog's recharge mode (`MechWorkModeRow.recharge`, native
`def == MechWorkModeDefOf.Recharge`), stays there until every mech is at the
band's upper bound, then returns to the role mode; with no charger ready it
does not enter, and a charging group leaves. Unread energy never counts as low.
A group the player set to Recharge by hand is treated like any manual edit (a
deficit to Auto, never a provenance hold; control-loop.md, Manual control): once
every mech is at the upper bound it returns to its role mode, as after a charge
the bot started. The system keeps no per-setting player-owned state, so none is
added here.
`MechChargerOwed` is the build side: a mechanitor exists and every standing
charger is busy (a charger full of waste is the pollution concern's; an idle unpowered one is the
power planner's, which wires every power consumer), so gestation should
not add a mech first. The charger definitions are the catalog rows with
`PlanningDefinition.MechCharger` (a `Building_MechCharger` thing class),
`observation.MechChargerDefs`.

`EnsureMechCharger` (`mech_charger_concern.go`, `rounds_mech_charger.go`) is the concern
for that need: a Standard in the Upkeep domain, assessed only where the Biotech
colony read and the mechs are known (`RoundsFacts.MechChargerOwed`), in deficit
while a charger is owed. Its one method builds the first catalog-flagged,
researched charger on the first footprint native previews as legal, safe and
reachable, ranked by `MechChargerSites` over the polluting-machine rule
`PollutionSites`: footprints lie wholly on known free ground (walkable,
unoccupied, in no zone, no doorway) at the catalog's `PlanningDefinition.Size`,
far from field zones, bedroom and barracks cells, dining and recreation room
cells and polluted cells, then near an atomizer. A charger blueprint, frame or
open plan holds it. Powering the charger is the power concern's; emptying its waste is
`ManagePollution`'s. Biotech concerns are bound only when assessed, so the store
counts `policy.BiotechConcerns` apart from the concerns every colony has.

`MaintainGeneBank` (`gene_bank_concern.go`, `rounds_gene_bank.go`) keeps every genepack in a
gene bank: a Genepack deteriorates unless it
sits in a powered bank (4 packs each). A Standard in the
Upkeep domain over the Colony fact family, assessed from the keyed Biotech
colony section: `GeneBankNeed` is unknown while the section, a bank's
capacity or a pack's whereabouts (map position or bank id) are unread, and
otherwise owed while more packs lie loose than the standing banks have free
slots (`RoundsFacts.GeneBankOwed`; a pack with no bank at all owes one). Its
one method builds the first available catalog bank (the def carrying
`CompProperties_GenepackContainer`, `bridge.GeneBanks`, never a name) on the
first free footprint native previews as legal, safe and reachable, searched
outward from the first gene assembler (a bank links to an assembler within 12.9
cells), else the first bank, else the production district. A bank blueprint,
frame or open plan holds it. Powering it is the power concern's (a bank is a 40 W
consumer wired like any other); harvesting, assembly and implanting are other
concerns.

`MaintainWorkLedger` (`work_ledger.go`, `work_ledger_concern.go`,
`rounds_ledger.go`) is the bot's production-bill ledger (epic #2590). A Standard in
the Industry department over the Colony fact family. The ledger is memory only
(`Rounder.ledger`): a planner that has migrated implements
`buildingruntime.OrderDeclarer` (`DeclareOrders(ctx, snapshot, projection, benches)` returns a
`policy.Declared`: its whole wanted `OrderSpec` set, or `Abstain` when it lacks
facts) and commits no bill method of its own. Each review calls every declarer,
reads every bench's bills through the bench census (`ReadGearBenches`;
`policy.LedgerActuals` is unknown while any bench's bills or any bill's spec is
unread), and runs `policy.ReconcileLedger`. The finding is `RoundsFacts.LedgerOwed`:
owed exactly while the diff places or removes anything, unknown with no
declarer or an unread readback (so a ledger with no declarers never touches a
bill). Orders are identified as native identifies bills: recipe, ingredient
filter, worker pin and repeat mode with its count (`OrderSpec.Key` uses the wire
class, so a food target equals a stock target); no native tag. A bill is
identified across Rounds by its native `UniqueLoadID`, which vanilla scribes
with the bill, so ids survive Rounds and save/load; a restart empties the
orphan counters and only delays removal. The ledger planner commits the review's
plan as one attempt-numbered owner method of `production_bill` and
`remove_production_bill` actions (removals first, at most
`store.MaxBillPlanActions`), under one multi-key arbiter claim (every bench and
each removed bill id), at most once per review and never while an earlier plan of the
owner is open (an unknown receipt blocks until its resend resolves).

The bench dispatcher (`work_dispatch.go`, `work_dispatch_memory.go`; pure policy,
memory in `Rounder.ledger.dispatch`) sizes and places the orders. A declared order
carries `OrderSpec.Product` and `Class` beside the spec (not in `Key`); with them a
stock target is a rate-model order. Its deficit is target minus the projection's
stock count, its demand deficit over the class refill window, its per-bench capacity
the recipe work and bench work speed over an average eligible worker's hours and
speed (`DispatchWorkers`: available pawns with the recipe's work type enabled), and
the benches wanted `BenchesWanted`, capped at the usable benches that offer the
recipe and at the workers. That count (plus calibration widening, at least one) is
`ReconcileLedger`'s copy count for the spec; an unsized order (not a stock target,
or stock, schedule or recipe work unread) stands on one bench. `PlaceLedgerOrders`
puts each copy on the fastest eligible bench (work speed, then fewest bills, then
id) that does not already carry it and has fewer than `LedgerBenchSlots` readback
bills; a copy no bench takes is unplaced. Calibration keeps a rolling one-game-day
window per stock target, restarted when the number of carrying benches changes and
reset with the world: a full window whose stock gain is under half of the predicted
gain (and whose stock never reached the target) widens by one bench, capped at the
usable benches, unless the recipe's ingredient stock is at or below one unit
(`ingredient_bound`) or every carrying bill is active, ingredients are present and
no pawn's job targeted a carrying bench all window (`haul_bound`); with a bill on
every eligible bench it reports `benches_exhausted`. `RoundsFacts.UnmetThroughput`
is one row per bench kind (`policy.UnmetThroughput`: units a day short and the
reason, also `no_bench` when the model wants more benches than exist and
`bench_slots_full` for an unplaced copy with capable benches), the demand signal the
facilities ladder reads. A finished bill is spent and never satisfies an order.

Orphan removal covers only bills the journal (`store.PlacedBills`) says a migrated
owner placed (`policy.LedgerMigratedOwner` sets `ActualBill.Migrated`): a bill an
unmigrated owner placed, one no plan placed and a player's are kept until the
cleanup child (#2605). The first migration (#2597) is `MaintainEquipment`:
`RoundsGearPlanner` declares the apparel batches and `RoundsArmoryPlanner` the
weapon batches, armor ladder and mortar-shell stock (`policy.DeclareGearOrders`,
`DeclareArmoryOrders`); both keep only their non-bill work (wear orders, apparel
policies, the crafting spot). An existing bill that makes a need is declared as it
stands (so its spec key never drifts), a new batch is declared only while no wear
candidate is pending, and the declared batches are the ingredient demand of
`openBills`. `production/gear-ledger` is the nightly signal.

`MaintainResource` (#2601) is migrated too: `RoundsResourcePlanner` declares
`policy.DeclareResourceOrders` (a stock-target order, with `Product` and
`Class` for the dispatcher, per floor below its target, the beer reserve as a
`BeerReserve` order, and a standing bill that makes a floor's resource as it
stands) and commits no bill method. Its mining, sourcing, field and trade
methods are unchanged; where the Round's supply plan opens a produce candidate
the step lends the clock a window and the ledger places the bill. The old
one-method-per-recipe history and the ingredient-credit cap on the bill target
are gone: the order is judged again every Round at the floor's full target.

`MaintainArt` (#2603) migrates the worker-pinned sculpture batches:
`RoundsArtPlanner.DeclareOrders` (`policy.DeclareArtOrders`) declares, while an
inspired artist, an owed bedroom or a sale need is open, every active sculpture
bill pinned to a current artist as it stands plus the bill the art selectors
choose for each artist lacking one. A sculpture pinned to someone who is no longer an
artist, or any sculpture once the need is known gone, is undeclared and so
removed after the grace rounds; an unread need or artist abstains. The planner
keeps only the game time a sculpture takes (`artNativeWorkTicks`). The produce-item quest
decrees of `MaintainPopulation` declare through
`RoundsPopulationJoinerPlanner.DeclareOrders` (`policy.DeclareDecreeOrders`): the
funded batch, or the bill already making the item as it stands; the planner keeps
the crafting game time and the harvest and hunt decrees.

Beer reserve and human
butcher bills read back as plain target and forever bills until native reports a
bill class.

`policy.PlanMechGuards` orders every standing guard at the hostile nearest to it
among those within `MechCommandRange` (25 tiles, Mechanitor wiki; native
`MechanitorUtility.InMechanitorCommandRange` stays authoritative) of its
overseer: the existing combat batch's draft (when undrafted) and `attack` order.
Both are pure over recorded facts (`mech_control_test.go`) and fed from the pawn
table, which already holds every spawned pawn with its `PawnBiotech` block
(`observation.MechFleet`: living colonist mechanitors, living mechs). The routine
read carries the fleet as `Projection.Mechs`; `RoundsWorkPlanner` appends
`PlanMechControl`'s settings to the work plan's `PawnSettingsAction`s, with the
Biotech catalog from the frame. The combat frame's pawn cut keeps mechanitor and
mech rows (and `Combat.Catalog` is read when one is present), so each fight stop
appends `PlanMechGuards`'s drafts and attack orders to its `combat.orders` batch;
the drafts join the fight roster and are undrafted when it closes.

## Mech gestation

`MaintainMechs` (`policy/mech_gestation.go`, planner flag `mechs`) queues
one gestation bill at a time: a `Bill_Mech` as a single-count `GearBatch`
production bill on a gestator (the `mech` branch of the production bill write).
`MechGestationOwed` raises the concern while a gestator is idle, no waste is
uncleared, a charger is ready (`MechChargerReady`: chargers before more mechs)
and `NextMech` finds a kind a mechanitor can afford. The rules:

- Bandwidth gates: a kind is built only when `TotalBandwidth - UsedBandwidth -
  GestationBandwidth` (the pawn row's mechanitor block) covers its catalog
  `bandwidth_cost`; native re-checks the game's `HasBandwidthForBill`.
- Waste holds gestation: a gestator holding waste, or a wastepack stack that is
  not frozen, not in an atomizer and not dissolved (count 0), or any unread
  count or flag, blocks the next bill (waste cleanup is the pollution concern's).
- Colonist need picks the role through `MechRoleNext` over the work roster and
  the mechs the mechanitors control (`bridge.ReadMechs`, one pawn read by id);
  a worker is bought only if it covers a short, uncovered work type, a guard
  by combat power per bandwidth. Cost, then name, break ties; a bulk recipe
  (`BulkRecipe`) wins among recipes of one kind.
- A mech kind a gestation recipe makes that the catalog lacks is an error.

Acceptance is table-driven in `mech_gestation_test.go`: every bandwidth
combination stays inside free bandwidth, waste cases hold, one gestation at a
time.

## Acceptance

Planner behaviour is table-driven in `work_assignment_test.go`,
`pawn_profile_test.go` and `pawn_schedule_test.go` (trait flags, floors,
growth secondaries, forbidden roles, decay, twelve-pawn coverage, three-review
stability, timetable templates) and `construction_helpers_test.go` (helper
restrictions, withheld and unknown work, hold and restoration). `internal/snapshot/workers_test.go` replays the three debug-start
colonists' pawn reads recorded from native runs, with seeded sheet and written readback: a major passion owns a tied
kitchen with the other cook backing it at 2; Pyromaniac/Brawler/Abrasive
never fight fires, hunt or warden while Industrious wins a tied Construction
sheet; every core role is owned once and the written matrix replans
unchanged; a NightOwl's day sleep, a QuickSleeper's six-hour sleep and a
hand-edited timetable replanned; two pawns under the Construction floor
help at 4 beside six walls. `takeover/schedule` still writes a timetable
through a real `WorkSettingsIntent` and reads it back natively.

## Drug policy

The work routine gives each colonist its own drug policy, labelled with its
short name, through a `DrugPolicyIntent` and `PawnSettingsIntent.drug_policy`;
the [`drug_policy` action](action-contracts.md) states the contents.

After Brewing finishes, MaintainResource requests 12 beer and 12 smokeleaf joints.
Once food fields are sufficient, the field planner adds at most nine cells each
of hops and smokeleaf, accounting for existing fields and native season, soil and
sowing availability. These crops never contribute food coverage. The workshop
ladder builds a fermenting barrel and a discovered wort-recipe bench. A saved
`SocialBeerBill` counts loose wort, barrel contents and finished beer against its
reserve, so fermentation does not cause continuous wort production. Native recipe
batch size can overshoot the target. Hauling, brewing, fermentation and consumption
remain ordinary game work; actual mood/recreation recovery is a separate check.
