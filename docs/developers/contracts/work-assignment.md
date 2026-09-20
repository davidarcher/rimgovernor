# Work assignment contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

The work planner (`policy.PlanWork`, `go/internal/policy/work_assignment.go`)
turns the routine read's pawns into one priority matrix per review: every work
type native reports, every available colonist whose work applies. It is a
proposal compared against the readback (`Matches`), never permission to change
a pawn's settings; the work review dispatches the differences through
`WorkBoundary.AssignWork` under the pawn's snapshot token, and a player
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
role-based timetable from the same profile: the native day (Sleep 22h-5h)
with a two-hour Joy block at 18h-19h; a NightOwl works 23h-6h, plays 21h-22h
and sleeps 10h-17h; a QuickSleeper's Sleep block shrinks to six hours. Every
known timetable is planned, whoever wrote it: a timetable edited under Manual
is replanned like any other once Auto holds (control-loop.md, Manual
control; #461); an unknown timetable (issue `schedule`) is skipped.

The work review sends a mismatching timetable in the pawn's `PatchPawn`
(`domain.NewScheduleAssignment`, `Schedule.assignment_defs` all 24 hours,
`SettingsField.Schedule` in the receipt) under the same snapshot token as the
priorities, so a timetable edit between read and write refuses the whole
pawn. Native (`NativeWorkSettings`) hashes the current 24 def names into the
token, requires every `TimeAssignmentDef` and a 24-slot tracker, writes through
`Pawn_TimetableTracker.SetAssignment`, and reads the timetable back into
`Matches`. `RoutineFacts.WorkCoverage` is false while any planned timetable
differs from the readback.

## Acceptance

Planner behaviour is table-driven in `work_assignment_test.go`,
`pawn_profile_test.go` and `pawn_schedule_test.go` (trait table, floors,
growth secondaries, forbidden roles, decay, twelve-pawn coverage, three-review
stability, timetable templates). The `workers/*` native cases
(`nativeaccept/cases/workers`, `WorkersFixture`'s `test/workers_setup`) seed
the three debug-start colonists with a flat sheet, no traits, manual
priorities and the native timetable, then one scenario each: `workers/passion`
(a major passion owns a tied kitchen, the other backs it at 2),
`workers/traits` (Pyromaniac/Brawler/Abrasive never fight fires, hunt or
warden; Industrious wins a tied Construction sheet), `workers/coverage` (every
core role owned once, Capacity true, the written matrix matches on readback
and replans unchanged) and `workers/nightowl` (the first native schedule
write: a NightOwl's night shift and a QuickSleeper's six-hour sleep beside the
work rows, a hand-edited timetable replanned and rewritten). Each writes through the real
`PatchPawn` execute under the work snapshot token and reads the sheet back
through the routine census's pawn observation.

## Social drug policy

The autonomous work routine assigns `RimGovernor social drugs` through
`SetDrugPolicy`, using the same durable settings actions and pawn snapshot CAS
as work assignment. Native execution creates or updates the named policy,
assigns the pawn and makes it the colony default. Only Beer and SmokeleafJoint
are permitted for recreation; addiction use, scheduled consumption and inventory
carry are disabled for every drug. A changed assignment is replanned from fresh
facts. The settings snapshot includes the drug configuration and colony default.

After Brewing finishes, MaintainResource requests 12 beer and 12 smokeleaf joints.
Once food fields are sufficient, the field planner adds at most nine cells each
of hops and smokeleaf, accounting for existing fields and native season, soil and
sowing availability. These crops never contribute food coverage. The workshop
ladder builds a fermenting barrel and a discovered wort-recipe bench. A saved
`SocialBeerBill` counts loose wort, barrel contents and finished beer against its
reserve, so fermentation does not cause continuous wort production. Native recipe
batch size can overshoot the target. Hauling, brewing, fermentation and consumption
remain ordinary game work; actual mood/recreation recovery is a separate check.