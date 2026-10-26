# Work assignment contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

The work planner (`policy.PlanWork`, `WorkAssignment.cs`)
turns the routine read's pawns into one priority matrix per review: every work
type native reports, every available colonist whose work applies. It is a
proposal compared against the readback (`Matches`), never permission to change
a pawn's settings; the work review dispatches the differences as a
`WorkSettingsIntent` on Actions/Apply. The plan owns every
priority: no player edit is exempt, and a differing readback is replaced.

The admin work-assignment review names its lifecycle: `AwaitingPlanner` before
coverage is planned, `Settled` when no assignment, setting or coupled activity is
owed, and `Owed` otherwise. A known owed setting remains owed before coverage is
planned. The Need requires a completed review of its current world; a previous
world's settled review cannot certify it.

Admin family inputs use named states: burial and stockpile sections are
`NotComposed`, `Settled` or `Owed`; the ledger starts `Unread` and work assignments
start `AwaitingPlanner`. A composed Need requires its completed current review.
Neither an absent section nor an invalid enum value can certify it as met.

## Pawn profile

`policy.BuildProfile` reads a `PawnProfile` from the same `WorkPawn` the review
already carries (`Work.cs`): the actual native skill rows, including learning evidence, with the effective
level, the stored level beneath aptitude, passion and disabled flag; traits with
degree; the backstory-incapable work types; biological age (`Child` from the Biotech developmental stage).
The profile retains the complete canonical native pawn row. Identity, skills,
incapable work types, age, inspiration and timetable hours are read from that row;
catalog-derived trait, gene and work values stay alongside it. Missing required
native inputs fail the read. An absent skill is explicitly missing, and absent
Biotech is the named inactive state.

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

The thought- and stat-derived flags (`NightShift`, `MeleeOnly`, `FrontLine`,
`RearRanged`, `Pyromaniac`, `Execution`, `SurgeonSafe`, `Nudist`, `Ascetic`,
`ChemicalInterest`, `Greedy`, `Jealous`) read the thoughts a trait nullifies or
is required by, and `Gourmand` a hunger-rate factor above 1. The one effect no
row states, `Sociable` (Kind +1, Abrasive -1), is the registered
`sociableTraits` table in `TraitWorth.cs`. A trait the catalog does not carry
fails the read. The gear
([equipment upkeep](equipment-upkeep.md)), drug and room concerns read their flags
from `PawnProfile.Effects`; mood control keeps its native thresholds.

A prisoner prospect's trait is good or bad by the sign of its worth
(`DefinitionCatalog.TraitBalance`, weighed by `policy.TraitBalance.Worth`): stat
offsets and factors (distance from 1), hunger rate, disabled work types,
sociability and mood (always-active thought offsets, and the worst negative stage
of each situational thought the trait requires). The weights are the one table
in `TraitWorth.cs`.

`PawnRules.SkillLearnFactor` is the passion multiplier (none 0.35, minor 1.0,
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
  prisoner is held; `WorkDemand` reads the census from the routine facts)
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
  at 2 are the next-best backup when the type has demand. The roster never
  assigns work to train a skill: practice and maintenance are chosen by the
  coupled selection and executed through Hands, so a pawn owning nothing is
  not given a slot to keep a skill exercised.
- **Everyone capable** takes 3 (level 8 or unskilled work) or 4 under manual
  priorities, enabled in checkbox mode. Firefighter, Patient, BedRest and
  Childcare stay 1; Hauling, Cleaning and BasicWorker 3, Hauling and Cleaning
  4 for the research owner. The mod's `RimGovernorTraining` work type
  (range drill, [facilities](../architecture/facilities.md)) has no relevant
  skill, so every capable colonist takes 3: the matrix needs no training row,
  because the native work giver offers a lane only to a colonist under the
  combat skill target with budget left.

`WorkDecision.Coverage` lists demand, owners and capable count per type
(`ReviewFacts.WorkRoster`: `WorkRosterRead.Read` once the planner planned, `Unread` otherwise;
`ReviewFacts.WorkCoverage` is the `WorkCoverageState` Unread, Matched or Unmatched).
The pawn census the planner reads is `ReviewFacts.ForecastObserved.WorkPawns`, a `WorkPawnsRead` containing every colonist's row. A complete census with no colonists is empty; an absent native census fails the read. `Capacity` is false when a required type or a core
role (Doctor, Cooking, Construction, Growing) found no owner. `Matches`
compares the proposal to the readback with checkbox semantics when manual
priorities are off (enabled or not, never the rank).

## Ongoing work and shared occupancy

For supported [care and deliveries](support-work.md), the shared portfolio
chooses exact self-service or helper work before admission through the existing
WorkAssignments Concern. It uses the same pawn and resource claims as production;
the priority matrix is not a second support-work selector. Legacy bench-cleaning,
refill-withholding and global chunk-haul policies are removed.

The roster and ColonyForecast use `ForecastCurrentWorkOf` to interpret the current
job. A known idle job has zero remaining productive work; an unread job has unknown
occupancy. An observed non-idle job is occupied, with unknown remaining ticks unless
the forecast caller supplies an estimate. `WorkDecision.CurrentWork` exposes these
facts; an unknown duration never becomes a free workday. Work claims protect every
observed job target with `pawn_target_claimed`. Native reservation checks preserve
those holders: admission cannot evict another pawn merely because its work could
be interrupted by a direct order. The obsolete interruptibility flag is removed
from observations and journal admission evidence (schema 61).

An enabled, eligible, demanded ordinary job protects its pawn from becoming the
primary owner of another work type with equal or lower native natural priority.
Other capable pawns can take that role. Within a type, ongoing work ranks before
fitness; Construction still ranks raw skill first. Higher native natural-priority
work can take an owner, and medical rest still overrides ordinary work. Changed
requirements, disabled work, job completion or cancellation remove protection on
the next read. No separate hold is saved.

Capabilities and assignments remain separate: a protected cook may be capable of
Construction but contribute no primary Construction owner. A backup remains enabled
at priority 2, including when every candidate is occupied. This avoids disabling
work merely because its preferred worker is currently busy. Both live review and
dispatch call the same `PlanWork`, so readback comparison uses this rule too.

This is current-role protection, not timed scheduling. Idle pawns can still own
multiple roles; higher-priority work can interrupt, and native job selection owns
execution. `Capacity` reports primary-role coverage, not proof of completing all
work within a horizon. Shared timed windows and the bounded activity comparison
remain the [ColonyForecast](../architecture/colony-forecast.md#review-inputs-and-pawn-windows)
components for that proof; live route/duration/needs producers and timed dispatch
remain unfinished. There is no new demand calculation or order writer.

## Ready work

`ProjectReadyWork` (`ReadyWork.cs`) projects plans into diagnostic
candidates (`ReviewState.ReadyWork`); admission does not read it. Parallelism is the
observed count of eligible workers or claims, with no constant cap. An action
kind without a stage adapter (everything but Building, Haul, CutPlant,
AreaPlantCut, ProductionBill, GrowerCrop, ZoneCreate, SupplyAllow and
SupplyForbid, and a bill whose recipe `RecipeFacts` does not know) reports
`awaiting` with reason `eligibility_unknown` and no work or parallelism; it is
never guessed from the goal's labor profile and adds no demand. Dependencies
and holds still read `blocked`.

## Construction helpers

Owners stay the skilled constructors; `WorkDemand.Help`
(`ConstructionHelpers.cs`) adds bounded help below the floor. Demand
is `ConstructionHelpDemand` over ready work, the open plans' building
definitions (with their observed `ConstructionSkill`) and the current native
construction census. There is no definition-name list: every building
definition in play (an observed site, an open plan, a construction candidate)
is classified from observed facts.

- Quality-free (`QualitySensitive` false) with an observed skill
  prerequisite (the plan definition's, else the site's native finishing
  skill) is helper work. The parallelism of its runnable `building:<def>`
  candidates is demand, and the highest such prerequisite is the helper floor.
- Quality-sensitive with a planner-set finishing `Minimum` is protected:
  native enforces the setting per target, so it neither counts as demand nor
  moves the floor.
- Anything else withholds helpers and withdraws current ones at once, under
  the reason naming the fact: `construction_prerequisite_unknown` (also a
  construction candidate with no `building:` definition),
  `construction_quality_unknown` (no observed site)
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
The complete construction census supplies a ready-work count even without a
previous report. A report from another world contributes no actions. A missing
census fails helper planning. A frame placed between two reviews follows vanilla
rules until the brain adopts it.

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
ownership and consumes no pawn labor: the choice rides the work Method beside its
assignments, and admission does not count it as a work assignment.

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
(`ReviewState.Roster.Help`), which the next review reads back for the
hysteresis; `no_sustained_idle_pawn` and `no_unmet_suitable_construction`
name why spare capacity went unused.

## Situational roles

`PawnRoles.cs` answers the questions other concerns ask of the
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

Supported rest and recreation timing comes from the shared needs trajectory and
portfolio comparison, followed by exact PawnActivities through Hands. The old
role, trait and need-band timetable writer is deleted. A timetable is observed
native state, not a competing instruction to a controlled pawn.

The existing WorkSettings schedule operation remains for explicit operations and
player-authority mechanics. Independent quest/rate consumers still read observed
work hours and preserve unknown timetables; these observations do not schedule
supported colony work. The layout comfort estimate has a fixed occupancy
assumption, explicitly separate from the selected needs timeline.
## Mech control

`policy.PlanMechControl` (`MechControl.cs`) puts each mechanitor's
mechs in role groups and sets each group's mode through the `mech_control_group`
and `mech_work_mode` arms of `PawnSettingsAction`: every move first, then each
group's mode (the mode is per group). A work mech (the race's `IsWorkMech`) is a
worker, any other kind a guard. With two or more control groups (vanilla default
2) workers go to the group already holding most of them, guards to the busiest
other group; workers run `Work`, guards `Escort` (the wiki's "do available work
tasks" and "follow the mechanitor and fight enemies"; `MechWorkModeDefOf` also has
`SelfShutdown`, unused here). The brain resolves `Work`, `Escort` and `Recharge`
as rows of the def mirror's `MechWorkModeDef`s; the catalog fails to decode if one
is missing. The plan fails without those modes or any mech's kind. A mech with no overseer among the
inputs, or an unread group, is left alone.

Colonist need versus bandwidth, decided from the read: control never changes
bandwidth, which only acquiring mechs spends (gestation). Colonist need
decides what the next free bandwidth buys (`MechRoleNext`): a worker while a
`WorkCoverage` has fewer owners than demand, no work mech of the mechanitor
covers that work type and a catalog work mech kind lists it; a guard otherwise;
nothing with no free bandwidth. With a single control group threat beats work:
`Escort` while a hostile is on the map or the group holds only guards, `Work`
otherwise.

Gestation and recharge retain the native Biotech colony records directly. A gestator is active when its bill relation is present; a charger is busy when its charging-mech relation is present. Waste policy reads the same native wastepack rows, without separate projected copies.

Recharging (`MechRecharge.cs`) rides the same group mode. Each mech row
carries `PawnMech.energy` (`Need_MechEnergy`, 0-1) and its control group's own
recharge band (`recharge_below`/`recharge_above`, the private
`MechanitorControlGroup.mechRechargeThresholds` the game recharges within), so
no threshold is a bot number. With a charger ready (`MechChargerReady`: powered,
not full of waste) a group with any mech under its band's lower bound runs the
game's recharge mode (native
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

`biotech/mech-charger` (`BiotechFamily.cs`, `Biotech.cs`, #3261) is the Need
for that: derived only while the Biotech colony section is active (the mech fleet is required,
`MechRules.MechChargerOwed`), unmet while a charger is owed, and a precondition of
`biotech/mechs`. Its one Method per charger count builds the first catalog-flagged,
researched charger on the first footprint native previews as legal, safe and
reachable, ranked by `MechChargerSites` over the polluting-machine rule
`PollutionSites`: footprints lie wholly on known free ground (walkable,
unoccupied, in no zone, no doorway) at the catalog's `PlanningDefinition.Size`,
far from field zones, bedroom and barracks cells, dining and recreation room
cells and polluted cells, then near an atomizer. A charger blueprint, frame or
open plan holds it. Powering the charger is the power concern's; emptying its waste is
the `biotech/pollution` Need's.

`biotech/gene-bank` (`GeneBankConcern.cs`, `Biotech.cs`, #3261) keeps every genepack in a
gene bank: a Genepack deteriorates unless it
sits in a powered bank (4 packs each). A Need over the Colony fact family, read
from the keyed Biotech colony section: derived only while the section is active, and owed while
more packs lie loose than the standing banks have free slots
(`GeneBankConcernRules.GeneBankOwed`; a pack with no bank at all owes one). Its
one Method per bank count builds the first available catalog bank (the def carrying
`CompProperties_GenepackContainer`, `bridge.GeneBanks`, never a name) on the
first free footprint native previews as legal, safe and reachable, searched
outward from the first gene assembler (a bank links to an assembler within 12.9
cells), else the first bank, else the production district. A bank blueprint,
frame or open plan holds it. Powering it is the power concern's (a bank is a 40 W
consumer wired like any other); harvesting, assembly and implanting are other
concerns.

The `admin/work-ledger` Need (`WorkLedger.cs`, `ColonyAdmin.cs`,
`Ledger.cs`, #3304) is the bot's production-bill ledger (epic #2590); it replaced the
`MaintainWorkLedger` Concern. The ledger is memory only
(`ColonyReview.ledger`): a planner that has migrated implements
`buildingruntime.OrderDeclarer` (`DeclareOrders(ctx, snapshot, projection, benches)` returns a
`policy.Declared`: its whole wanted `OrderSpec` set, plus an `Abstain{Owner, Fact}` per
owner that lacks a fact: `policy.Abstaining(Cause)` is the only constructor, the `CauseUnread*` members of `policy.Cause` are a
closed typed set, and the declarer stamps the owner and every order's `OrderSpec.Owner` with
`Declared.For(filingKey)`. The owner is a filing-key string (decision 21): a Need's `FilingKey.Value` or an
unmigrated Concern's id, so Needs and Concerns share one ledger. Owner is not part of `Key`; two owners declaring one spec coalesce into one
order that lists both. Any abstain, of any concern, still skips orphan removal for the Round) and commits no bill method of its own. Each review calls every declarer
(together, since #3566: each declares from the same reading and writes only its own memory, so their native hops share
frames; the declared list keeps the composition order, and the first declarer to fail, in that order, fails the review as before),
reads every bench's bills through the bench census (`ReadGearBenches`;
`policy.LedgerActuals` is unknown while any bench's bills or any bill's spec is
unread), and runs `policy.ReconcileLedger`. The finding is the Need's
(`ColonyReview.LedgerOwedIn`): unmet exactly while the diff places or removes anything, unknown with no
declarer or an unread readback (so a ledger with no declarers never touches a
bill). Orders are identified as native identifies bills: recipe, ingredient
filter, worker pin and repeat mode with its count (`OrderSpec.Key` uses the wire
class, so a food target equals a stock target); no native tag. A bill is
identified across Colony review by its native `UniqueLoadID`, which vanilla scribes
with the bill, so ids survive Colony review and save/load; a restart empties the
orphan counters and only delays removal. The Need's work step commits the review's
plan as one Need Method of `production_bill` and
`remove_production_bill` actions (removals first, at most
`store.MaxBillPlanActions`) whose MethodId hashes the diff, under one multi-key arbiter claim (every bench and
each removed bill id), at most once per review. Claim admission replaces the old whole-owner open-plan
gate: a diff over other benches is admitted beside an open one, the same diff continues, and a diff that
touches a bill an open Method claims (`bill/<bench>/<recipe>`, `bill/<id>`) is a claim conflict until it
resolves (an unknown receipt holds its claims until its resend resolves).

The bench dispatcher (`WorkDispatch.cs`, `WorkDispatchMemory.cs`; pure policy,
memory in `ColonyReview.ledger.dispatch`) sizes and places the orders. A declared order
carries `OrderSpec.Product` and `Class` beside the spec (not in `Key`); with them a
stock target is a rate-model order. Its deficit is target minus the projection's
stock count, its demand deficit over the class refill window, its per-bench capacity
the recipe work and bench work speed over an average eligible worker's hours and
speed (`TryDispatchWorkers`: available pawns with the recipe's work type enabled), and
the benches wanted `BenchRateRules.Wanted`, capped at the usable benches that offer the
recipe and at the workers. That count (plus calibration widening, at least one) is
`ReconcileLedger`'s copy count for the spec. An order the model leaves
`DispatchSizing.Unsized` (not a stock target, or stock, schedule or recipe work unread)
stands on one bench, as does one whose positive demand meets zero capacity
(`BenchesWanted.NoCapacity`: no worker, or a bench that makes nothing). `PlaceLedgerOrders`
puts each copy on the fastest eligible bench (work speed, then fewest bills, then
id) that does not already carry it and has fewer than `LedgerBenchSlots` readback
bills; a copy no bench takes is unplaced. Calibration keeps a rolling one-game-day
window per stock target, restarted when the number of carrying benches changes and
reset with the world: a full window whose stock gain is under half of the predicted
gain (and whose stock never reached the target) widens by one bench, capped at the
usable benches, unless the recipe's ingredient stock is at or below one unit
(`ingredient_bound`) or every carrying bill is active, ingredients are present and
no pawn's job targeted a carrying bench all window (`haul_bound`); with a bill on
every eligible bench it reports `benches_exhausted`. `ReviewFacts.UnmetThroughput`
is one row per bench kind (`policy.UnmetThroughput`: units a day short and the
reason, also `no_bench` when the model wants more benches than exist and
`bench_slots_full` for an unplaced copy with capable benches), the demand signal the
facilities ladder reads. A finished bill is spent and never satisfies an order.

The ledger's read-only view is in memory only and has one reader, the station planner. Each review leaves a
`policy.LedgerView` (`policy.BuildLedgerView`) in `ColonyReview.ledger.view`: the status (`no_review`, `inactive`,
`readback_unknown` or `reconciled`) and, once reconciled, the wanted orders in key order, each the spec as the
station planner keys it (recipe, ingredients, worker, mode, target, product). `StationFactsOf` reads them as the
`work ledger orders` fact: known (empty) for an inactive ledger, unknown before a review or while the readback was
unread. The view issues no native call. The dashboard view the launcher once showed (declarers and abstains, order
states and benches, calibration windows, orphans, the export gap and its scored candidates, each order's placement
outcome from a journal-seeded placement index) and the `ledger_rows` copy the store kept of it had no reader left
and were deleted under #3566.

Orphan removal covers every bill of a production recipe kind: one no plan placed (a
player's, or placed before a restart) is an orphan like any other, and the journal
keeps no placement record. The first migration (#2597) was `MaintainEquipment` (now the `military/equipment` Need):
`GearPlanner` declares the apparel batches and `ArmoryPlanner` the
weapon batches, armor ladder and mortar-shell stock (`policy.DeclareGearOrders`,
`DeclareArmoryOrders`); both keep only their non-bill work (wear orders, apparel
policies, the crafting spot). An existing bill that makes a need is declared as it
stands (so its spec key never drifts), a new batch is declared only while no wear
candidate is pending, and the declared batches are the ingredient demand of
`openBills`.

The second migration (#2604) is declare-only: `MedicalPlanner` declares
the `medical/reserves` Need's stock-target medicine bill (`policy.DeclareMedicine`) and
`CareBillDeclarer` the surgery part (the `medical/surgery` Need, declared while
the Need is unmet, #3258) bills (the baby food bill is now `BabyFoodDeclarer`'s, filed
under the `food/baby-feeding` Need, #3254, and the mech gestation bill
`MechBillDeclarer`'s, filed under `biotech/mechs`, #3261)
(`policy.DeclareSelection` over the unchanged selectors), each only while its concern
is workable; the old `BillPlanner` purposes are deleted. These bills are
placed and kept but never removed in the first pass (a gestation bill is the mech
being formed, a part or medicine bill serves a patient). Removability is
decided by the recipe kind, not by who placed the bill. The bench census sets
`GearBill.Kind` from `DefinitionCatalog.RecipeLedgerKind` (mech gestation, medicine,
a body part item, baby-edible food), restart-safe, and `ReconcileLedger` keeps such
a bill whatever its owner while still letting it satisfy a declared order. The census
lists every spawned `IBillGiver` building that is not a pawn, so gestators and part
benches are read back; a pawn's surgery bills are operations (`surgery` actions), not
bench bills, and are outside the ledger. Not carried over: stale part-bill removal
and the replacement of an inadequate baby food bill (an adequate new bill is placed
beside it), both waiting for orphan removal of these kinds.

The food bills (#2602) are the second migration: cooking (now the `food/cooking` Need, #3254), `MaintainFoodStorage` (now the `food-stores/storage` Need, #3251),
food supply (now the `food/supply` Need, #3254) and refrigeration (now the `food-stores/refrigeration` Need, #3280) declare to the ledger. The cooking,
reserve (preserve), butcher and cook-ahead `BillPlanner`s and the food-storage
upkeep planner declare through `policy.DeclareFoodOrders` (`FoodOrders.cs`): the
same selectors that once chose the next bill now run over the bench census read
without its bills, so the declaration is what should stand. A stock-target bill that
already covers the wanted target is declared as it stands (`adequateOrder`); a
purpose that is not owed, or has no bench, keeps its standing bills; an unread
fact or a food-plan channel not yet open abstains; the cook-ahead bill ends with the
solar flare. The food-plan gates, the purposes' verdicts and the reserve
work-lending ride on the declarers, not on catalog planners: `DeclareOrders` notes
each verdict on the request that declares (`purposeReporter`) and the ledger step
files it (`AdminReview.LedgerPurposeReports`; see below). Human-meat and survival-pack ingredient filters
ride in the declared spec. Food orders set no `Product`/`Class`, so each stands on
one bench until the resource census covers meals.

The food, art and export declarers are also `purposeReporter`s: no catalog
planner stands for them, and the admin family files their verdicts beside its
Need reports (`AdminReview.LedgerPurposeReports`, through `FamilyRun.Filed`). A purpose
whose concern is not workable reports no deficit; a purpose with open work of its
own reports existing work; otherwise it reports what its declaration noted
(awaiting the food plan, a missing cooking or butcher bench, an unread fact, or
`ledger_bill` once the ledger holds the bill) and lends game time while its
reserve, sculpture or export bill runs. A journal read that fails reports the
purpose unavailable and never fails bill placement. Art files under the
`rooms/art` Need's subject key `rooms/art/bills` and reads that Need's standing; export
files under the `economy/trade` Need's subject key `economy/trade/bills` (#3316). The four food
purposes file under subject keys of their own (`food/cooking/bills`,
`food-stores/storage/bills`, `food/supply/bills`, `food-stores/refrigeration/bills`), because
their owners carry the building, acquisition and storage notes: a bill verdict filed on
the Need or concern would compete with them for its one winning cause and relabel its
strip with another purpose's reason. A purpose that serves a Need reads the Need's
standing (`PurposeReport.Need`) instead of open work.

`economy/resource` (#2601) is migrated too: `ResourcePlanner` declares
`policy.DeclareResourceOrders` (a stock-target order, with `Product` and
`Class` for the dispatcher, per floor below its target, the beer reserve as a
`BeerReserve` order, and a standing bill that makes a floor's resource as it
stands) and commits no bill method. Its mining, sourcing, field and trade
methods are unchanged; where the Round's supply plan opens a produce candidate
the step lends the clock a window and the ledger places the bill. The old
one-method-per-recipe history and the ingredient-credit cap on the bill target
are gone: the order is judged again every Round at the floor's full target.

The `rooms/art` Need (#2603, #3255) migrates the worker-pinned sculpture batches (room
decoration only since #2607; sculptures to sell are the `economy/trade` Need's):
`ArtPlanner.DeclareOrders` (`policy.DeclareArtOrders`) declares, while an
inspired artist or an owed bedroom is open, every active sculpture
bill pinned to a current artist as it stands plus the bill the art selectors
choose for each artist lacking one. A sculpture pinned to someone who is no longer an
artist, or any sculpture once the need is known gone, is undeclared and so
removed after the grace rounds; an unread need or artist abstains. The declarer
keeps no game-time hint. The produce-item quest
decrees of the `joiners/decrees` Need declare through
`JoinersReview.DeclareOrdersAsync` (`policy.DeclareDecreeOrders`): the
funded batch, or the bill already making the item as it stands; the planner keeps
the crafting game time and the harvest and hunt decrees.

The `economy/trade` Need (#2607, #3316) answers `SilverShort`: while the gap is open,
`TradeExportPlanner.DeclareOrders` (`policy.DeclareExportOrders`) ranks
craftable sale goods and declares the winners as finite `GearBatch` orders
(priority 3) pinned to the best capable worker. One owner per bill spec:
`economy/resource` owns products with a floor; `economy/trade` owns floorless
sale goods, never acquires, and never declares a floor product. It spends only
`policy.UsableIngredients`; score is (expected sale value minus raw ingredient
value) per (crafting ticks plus ingredient units times acquisition ticks), the
produce prior of 60 pricing unsourced ingredients and the worker's skill
setting the expected quality. Buyers are the `_Buy*` generators of the reachable
trader kinds (`bridge.TraderKindBuys`; orbital comms options plus known
settlements); the cash cap is the low end of the best kind's silver range
(`TraderKindSilver`). Quantity is min(gap / expected sell price, cash cap), with
held stock, packed art, standing bills and in-flight goods counted against the
gap; standing placed export bills are declared as they stand so a Round never
flips them. `ExportGuard` (15-day horizon, committed draws including the
order's own and declared-unplaced orders) refuses an order whose ingredients
the colony needs; a finite-versus-renewable classification can replace it. Any
unread input abstains. The sale path: while the gap is open held export
products join `TradeNeed.Surplus` with retain 0 (in `inspectTrade` and the
trade selection, via the planner's in-memory `exportMemory`; the weapon surplus
can be one review stale), and gear surplus (`policy.GearSurplus`: gear held
minus demand, best quality kept for loadouts, lowest value sold first, apparel
and weapons) is sold the same way. The gap may stay open while benches are
slow; the other silver responses run in parallel. Sculptures stay finite
batches pinned to an artist; a non-pinned good could later become a
stock-target order (not built). A standing sculpture bill pinned to an artist
is declared by both concerns' selectors only by spec key, so coalescing leaves one.
No trade-depot siting changes: sale goods leave from the stock the trade planner
already reaches.

Beer reserve and human
butcher bills read back as plain target and forever bills until native reports a
bill class.

`policy.PlanMechGuards` orders every standing guard at the hostile nearest to it
among those within `MechCommandRange` (25 tiles, Mechanitor wiki; native
`MechanitorUtility.InMechanitorCommandRange` stays authoritative) of its
overseer: the existing combat batch's draft (when undrafted) and `attack` order.
Both are pure over recorded facts and fed from the pawn
table, which already holds every spawned pawn with its `PawnBiotech` block
(`observation.MechFleet`: living colonist mechanitors, living mechs). The routine
read carries the fleet as `Projection.Mechs`; `WorkPlanner` appends
`PlanMechControl`'s settings to the work plan's `PawnSettingsAction`s, with the
Biotech catalog from the frame. The combat frame's pawn cut keeps mechanitor and
mech rows (and `Combat.Catalog` is read when one is present), so each fight stop
appends `PlanMechGuards`'s drafts and attack orders to its `combat.orders` batch;
the drafts join the fight roster and are undrafted when it closes.

## Mech gestation

`biotech/mechs` (`MechGestation.cs`, `BiotechFamily.cs`, planner flag `mechs`) queues
one gestation bill at a time: a `Bill_Mech` as a single-count `GearBatch`
production bill on a gestator (the `mech` branch of the production bill write).
`MechGestationOwed` leaves the Need unmet while a gestator is idle, no waste is
uncleared, a charger is ready (`MechChargerReady`: chargers before more mechs)
and `NextMech` finds a kind a mechanitor can afford; with `biotech/mech-charger`
composed the Need also waits while a charger is owed. The bill is declared to the
work ledger only while the Need is ready. The rules:

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

Every bandwidth combination stays inside free bandwidth, waste cases hold, one
gestation at a time.

## Acceptance

Planner behaviour covers trait flags, floors, growth secondaries, forbidden roles,
decay, twelve-pawn coverage, three-review stability and
construction helpers (helper restrictions, withheld and unknown work, hold and
restoration). The three debug-start colonists' pawn reads are replayed recorded from native runs, with seeded sheet and written readback: a major passion owns a tied
kitchen with the other cook backing it at 2; Pyromaniac/Brawler/Abrasive
never fight fires, hunt or warden while Industrious wins a tied Construction
sheet; every core role is owned once and the written matrix replans
unchanged; two pawns under the Construction floor help at 4 beside six walls.
Generated NightOwl/QuickSleeper timetable templates are not an acceptance oracle
for ordinary needs. Shared forecast tests cover needs choices and actual occupied
time; native activity cases cover selected sleep/recreation completion and
protection. Explicit schedule writes remain a native operation, not an automatic
ordinary-pawn scheduler. See the [case disposition](voluntary-control.md#acceptance-disposition).

## Drug policy

The work routine does not create or assign drug policies. Automatic prevention,
dependency, addiction and recreation dosing would delegate voluntary selection
to a native scheduler without a supported shared Method. Existing configured
policies are observed, not rewritten; scheduled use and unsupported needs remain
explicit control blockers. The [`drug_policy` action](action-contracts.md) and
assignment operation remain available for explicit operations and native readback.
Undispatched drug writes belonging to an old work-assignment Method are cancelled
under that exact owner; completed operations and player policies are not undone.

After Brewing finishes, economy/resource requests 12 beer and 12 smokeleaf joints.
Once food fields are sufficient, the field planner adds at most nine cells each
of hops and smokeleaf, accounting for existing fields and native season, soil and
sowing availability. These crops never contribute food coverage. The workshop
ladder builds a fermenting barrel and a discovered wort-recipe bench. A saved
`SocialBeerBill` counts loose wort, barrel contents and finished beer against its
reserve, so fermentation does not cause continuous wort production. Native recipe
batch size can overshoot the target. These provisioning operations do not establish
controlled-pawn hauling, brewing or consumption. Each voluntary service needs a
supported shared Method; unsupported scheduled-drug requirements are explicit
control blockers. Native fermentation remains simulation. Actual mood/recreation
recovery is a separate check.

The 24-hour timetable length (`domain.ScheduleHours`) and the 0-4 priority range
(`domain.MaxPriority`: 0 disabled, 1 first to 4 last) are fixed by the game and
shared by every priority consumer; see [kept constants](kept-constants.md).

## Shared Method availability

Routine assignment excludes pawns already owned by activity Methods, using the
same journal projection consumed by food selection and material admission.
It does not assign a second role by counting that pawn's timetable again.
Existing construction skill/tier checks remain in force. See
[shared live work resources](../architecture/colony-forecast.md#shared-live-work-resources)
for reconciliation, unknown durations, bounds and remaining consumer exceptions.

The [needs candidate projection](../architecture/colony-forecast.md#needs-alternatives-and-future-capacity)
uses the same work-resource view and occupancy union. Its explicit
`ProtectTimetable = false` ignores legacy generated timetable slots for strategic
comparison, but never actual current work, player authority or journal claims.
Selected need effects return productive windows to the existing activity search;
there is no independent needs labor budget. Existing routine assignment consumers
may still inspect timetable availability, but the automatic timetable writer is
removed and those observations cannot start a competing voluntary job.

Pawn profiles carry the observed work hours, gene effects and work minimum ages directly. A pawn with no timetable has zero scheduled work hours; a malformed nonempty timetable fails the read. Routine and direct colony reads derive the same complete colonist profiles from the native pawn census.


Work and bench requirements are total once the frame is read. A project definition, open bill bench or recipe missing from its census fails the planner boundary; it never supplies an empty requirement. Station demand uses the current world's derived resource targets and fails before that review exists. The builder count is a plain count of available, capable construction workers, including zero. Throughput names NoRecipe, Missing and Stocked ingredient states; recipes taking no ingredients are Stocked.

Coupled activity generation reads the exact composed Need set. An uncomposed
reviewer enables no activities; synthetic reviews must supply the Needs they
intend to compare. Layout anchors are optional until a layout core exists, and
placement must use a present layout, room or power-consumer anchor.


## Shared requirement ownership

`WorkDemandRules` derives construction, open-bill, pending-production and research
work requirements in pure Policy. Merging keeps the highest skill floor for each
work type in first-seen order and retains its first named skill. Pending resource
or equipment production enables the relevant bench work before its bill exists;
wort work also covers a positive beer target. An equipment-only research ladder
contributes requirements only while that equipment Need is owed.

Runtime's `ProjectBillReads` gathers the existing admitted actions and one native
bench census, then passes their existing rows to Policy. Both colony review and
the work planner consume the same requirement functions. A required bench,
recipe or construction definition absent from that census remains a named read
failure, and no outstanding work means no bench read. This introduces no second
demand ledger or persisted work state.
