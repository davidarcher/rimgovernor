# Work assignment contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

The work planner (`policy.PlanWork`, `go/internal/policy/work_assignment.go`)
turns the routine read's pawns into one priority matrix per review: every work
type native reports, every available colonist whose work applies. It is a
proposal compared against the readback (`Matches`), never permission to change
a pawn's settings; the work review dispatches the differences as a
`WorkSettingsIntent` on Actions/Apply, and a player
`WorkOverride` (per pawn × work type) always wins.

## Pawn profile

`policy.BuildProfile` reads a `PawnProfile` from the same `WorkPawn` the review
already carries (`observation/routine_work.go`): skills with the effective
level, the stored level beneath aptitude, passion and disabled flag; traits with
degree; the backstory-incapable work types; biological age (`Child` under 13).
Unknown traits, incapable rows or age leave those parts empty and the planner
skill-only for that pawn; they never make the review unknown.

Traits map to typed effects through one table (`traitTable`, keyed by TraitDef
name and degree, Core values):

| Effect | Traits |
| --- | --- |
| `WorkSpeed` | Industriousness ±0.20/±0.35, Neurotic +0.20/+0.40 |
| `LearnRate` | FastLearner +0.75, TooSmart +0.75, SlowLearner −0.75 |
| `MoveSpeed` | SpeedOffset −0.2/+0.2/+0.4 |
| `GreatMemory`, `QuickSleeper`, `NightShift` | GreatMemory, QuickSleeper, NightOwl |
| `MeleeOnly`, `FrontLine`, `RearRanged` | Brawler; Brawler, Tough, Nimble; ShootingAccuracy ±1 |
| `NoFirefighting`, `Pyromaniac` | Pyromaniac |
| `Sociable` | Kind +1, Abrasive −1 |
| `Execution`, `SurgeonSafe` | Psychopath, Bloodlust; Psychopath |
| `Nudist`, `Ascetic`, `Cannibal`, `Gourmand`, `ChemicalInterest`, `Undergrounder`, `Greedy`, `Jealous` | the trait of that name (DrugDesire −1/1/2) |

A trait the table does not know (mod, DLC, or a mood/nerves spectrum native
already folds into break thresholds) contributes nothing. The gear
([equipment upkeep](equipment-upkeep.md)), drug and room goals read their flags
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
  prisoner is held; `RoutineWorkDemand` reads the census from the routine facts)
  and, for any other skilled type, one owner when a natural specialist exists
  (level 6 or a passion).
- **Capable** is not incapable, not trait-forbidden (Pyromaniac Firefighter,
  Brawler Hunting, Abrasive Warden), Hunting only with a ranged primary, and
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
(`RoutineFacts.WorkRoster`). `Capacity` is false when a required type or a core
role (Doctor, Cooking, Construction, Growing) found no owner. `Matches`
compares the proposal to the readback with checkbox semantics when manual
priorities are off (enabled or not, never the rank).

## Construction helpers

Owners stay the skilled constructors; `WorkDemand.Help`
(`construction_helpers.go`, #653) adds bounded help below the floor. Demand
is `ConstructionHelpDemand` over the review's recorded ready work (#645): the
parallelism of runnable `building:<def>` candidates on
`HelperConstructionDefinitions` (Wall, Door, SleepingSpot,
DoubleSleepingSpot, Campfire, Sandbags, PowerConduit: no native skill
minimum, no quality, cheap materials). Unmet demand is that parallelism
beyond the owners not held by another work type's job. A helper is a pawn
under the floor that the requirement's minimum (the native floor, else 0)
admits, not resting, idle
(`idleJob`) now and at the previous review, or already helping; helpers
take priority 4 (enabled in checkbox mode), previous helpers first, then
by level, at most one per unmet task. No other work type's floor moves.

A work-type priority enables every native construction job, not one wall.
Native still refuses a frame above the pawn's `constructionSkillPrerequisite`;
nothing native keeps a helper off a quality or expensive frame. So any known
construction outside the set (a ready candidate, a conservative-adapter
candidate that may build, or an open plan's building definition, player
plans included) withholds helpers and withdraws current ones at once
(`risky_construction_pending`); unknown or other-world ready work authorizes
nothing (`construction_demand_unknown`). A risky frame placed between two
reviews is open to an enabled helper until the next review: that is the
limit of the coarse setting.

When unmet demand clears, helpers hold for `ConstructionHelpHoldTicks`
(2500) from the last tick it held (`held_after_demand`), then return to the
ordinary 0; an override set meanwhile wins. The record (`Idle`, `Helpers`,
`DemandTick`, `Ready`, `Unmet`, `Reason`, `Risky`) rides the roster report
(`RoutineReview.Roster.Help`), which the next review reads back for the
hysteresis; `no_sustained_idle_pawn` and `no_unmet_suitable_construction`
name why spare capacity went unused.

## Situational roles

`go/internal/policy/pawn_roles.go` answers the questions other goals ask of the
roster from the same profiles, each a pure function returning the pawn and
whether one qualifies: `SurgeonFor(minimum, harvest)` (Medicine at least the
recipe's minimum and 4, Psychopath preferred for a harvest), `WardenFor(execution)`
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
hunter on a known roster. The medical review has no surgery dispatch in Go,
so `SurgeonFor` waits for one.

## Schedules

`policy.PlanSchedules` (`pawn_schedule.go`) gives each available pawn a
role-based timetable from the same profile: the native day sleeps 22h-5h
(#1314); a NightOwl sleeps 10h-17h and is free overnight; a QuickSleeper's
Sleep block shrinks to six hours. Joy is the hour right before sleep and
every other hour is Anything. The planner never writes Work: Work blocks
ignore rest and recreation and wake sleeping pawns (#1293). Every
known timetable is planned, whoever wrote it: a timetable edited under Manual
is replanned like any other once Auto holds (control-loop.md, Manual
control; #461); an unknown timetable (issue `schedule`) is skipped.

The work review sends a mismatching timetable in the pawn's
`WorkSettingsIntent` (`domain.NewScheduleAssignment`, `Schedule.assignment_defs`
all 24 hours, `SettingsField.Schedule` in the applied result) together with the
priorities. Native (`WorkSettingsActionHandler`) requires every
`TimeAssignmentDef` and a 24-slot tracker when it applies, writes through
`Pawn_TimetableTracker.SetAssignment`, and reads the timetable back into
`Matches`. `RoutineFacts.WorkCoverage` is false while any planned timetable
differs from the readback.

## Mech control

`policy.PlanMechControl` (`mech_control.go`, #1687) puts each mechanitor's
mechs in role groups and sets each group's mode through the `mech_control_group`
and `mech_work_mode` arms of `PawnSettingsAction`: every move first, then each
group's mode (the mode is per group). A work mech (`MechKindRow.work_mech`) is a
worker, any other kind a guard. With two or more control groups (vanilla default
2) workers go to the group already holding most of them, guards to the busiest
other group; workers run `Work`, guards `Escort` (the wiki's "do available work
tasks" and "follow the mechanitor and fight enemies"; `MechWorkModeDefOf` also has
`SelfShutdown`, unused here). The catalog must carry both modes, the recharge
mode and every mech's kind, else the plan fails. A mech with no overseer among the
inputs, or an unread group, is left alone.

Colonist need versus bandwidth, decided from the read: control never changes
bandwidth, which only acquiring mechs spends (gestation, #1686). Colonist need
decides what the next free bandwidth buys (`MechRoleNext`): a worker while a
`WorkCoverage` has fewer owners than demand, no work mech of the mechanitor
covers that work type and a catalog work mech kind lists it; a guard otherwise;
nothing with no free bandwidth. With a single control group threat beats work:
`Escort` while a hostile is on the map or the group holds only guards, `Work`
otherwise.

Recharging (`mech_recharge.go`, #1688) rides the same group mode. Each mech row
carries `PawnMech.energy` (`Need_MechEnergy`, 0-1) and its control group's own
recharge band (`recharge_below`/`recharge_above`, the private
`MechanitorControlGroup.mechRechargeThresholds` the game recharges within), so
no threshold is a bot number. With a charger ready (`MechChargerReady`: powered,
not full of waste) a group with any mech under its band's lower bound runs the
catalog's recharge mode (`MechWorkModeRow.recharge`, native
`def == MechWorkModeDefOf.Recharge`), stays there until every mech is at the
band's upper bound, then returns to the role mode; with no charger ready it
does not enter, and a charging group leaves. Unread energy never counts as low.
`MechChargerOwed` is the build side: a mechanitor exists and every standing
charger is busy (a charger full of waste is #1683's), so gestation (#1686)
should not add a mech first. The charger definitions are the catalog rows with
`PlanningDefinition.mech_charger` (`Building_MechCharger` thing class),
`observation.MechChargerDefs`.

`policy.PlanMechGuards` orders every standing guard at the hostile nearest to it
among those within `MechCommandRange` (25 tiles, Mechanitor wiki; native
`MechanitorUtility.InMechanitorCommandRange` stays authoritative) of its
overseer: the existing combat batch's draft (when undrafted) and `attack` order.
Both are pure over recorded facts (`mech_control_test.go`) and fed from the pawn
table, which already holds every spawned pawn with its `PawnBiotech` block
(`observation.MechFleet`: living colonist mechanitors, living mechs). The routine
read carries the fleet as `Projection.Mechs`; `RoutineWorkPlanner` appends
`PlanMechControl`'s settings to the work plan's `PawnSettingsAction`s, with the
Biotech catalog from the frame. The combat frame's pawn cut keeps mechanitor and
mech rows (and `Combat.Catalog` is read when one is present), so each fight stop
appends `PlanMechGuards`'s drafts and attack orders to its `combat.orders` batch;
the drafts join the fight roster and are undrafted when it closes.

## Mech gestation

`MaintainMechs` (#1686, `policy/mech_gestation.go`, planner flag `mechs`) queues
one gestation bill at a time: a `Bill_Mech` as a single-count `GearBatch`
production bill on a gestator (the `mech` branch of the production bill write).
`MechGestationOwed` raises the goal while a gestator is idle, no waste is
uncleared and `NextMech` finds a kind a mechanitor can afford. The rules:

- Bandwidth gates: a kind is built only when `TotalBandwidth - UsedBandwidth -
  GestationBandwidth` (the pawn row's mechanitor block) covers its catalog
  `bandwidth_cost`; native re-checks the game's `HasBandwidthForBill`.
- Waste holds gestation: a gestator holding waste, or a wastepack stack that is
  not frozen, not in an atomizer and not dissolved (count 0), or any unread
  count or flag, blocks the next bill (the wastepack research in epic #1667;
  cleanup is #1683).
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
`pawn_profile_test.go` and `pawn_schedule_test.go` (trait table, floors,
growth secondaries, forbidden roles, decay, twelve-pawn coverage, three-review
stability, timetable templates) and `construction_helpers_test.go` (helper
restrictions, risky and unknown work, hold and restoration). `internal/snapshot/workers_test.go` replays the three debug-start
colonists' pawn reads recorded from the retired `workers/*` native cases
(#748), seeded sheet and written readback: a major passion owns a tied
kitchen with the other cook backing it at 2; Pyromaniac/Brawler/Abrasive
never fight fires, hunt or warden while Industrious wins a tied Construction
sheet; every core role is owned once and the written matrix replans
unchanged; a NightOwl's day sleep, a QuickSleeper's six-hour sleep and a
hand-edited timetable replanned; two pawns under the Construction floor
help at 4 beside six walls. `takeover/schedule` still writes a timetable
through a real `WorkSettingsIntent` and reads it back natively.

## Drug policy

The work routine gives each colonist its own drug policy, labelled with its
short name, through a `DrugPolicyIntent` and `PawnSettingsIntent.drug_policy`
(#1537); the [`drug_policy` action](action-contracts.md) states the contents.

After Brewing finishes, MaintainResource requests 12 beer and 12 smokeleaf joints.
Once food fields are sufficient, the field planner adds at most nine cells each
of hops and smokeleaf, accounting for existing fields and native season, soil and
sowing availability. These crops never contribute food coverage. The workshop
ladder builds a fermenting barrel and a discovered wort-recipe bench. A saved
`SocialBeerBill` counts loose wort, barrel contents and finished beer against its
reserve, so fermentation does not cause continuous wort production. Native recipe
batch size can overshoot the target. Hauling, brewing, fermentation and consumption
remain ordinary game work; actual mood/recreation recovery is a separate check.