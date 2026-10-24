# Native skill learning and decay

[Contract map](README.md) · [Pawn activities](pawn-activities.md)

`Skill.Learning` in normal biography observations carries frame/tick provenance,
observed stored level, aptitude, XP and today's XP, disabled/tracker eligibility,
native learning factors and per-level thresholds/decay. `WorkSkill.Learning`
preserves this evidence through ordinary observation decoding. Missing evidence
is unknown; zero decay, a disabled skill, an exhausted budget and an absent
mechanic are different facts. This observation does not create a durable XP ledger.

`ForecastSkill` consumes that evidence plus one ordered list of learning callbacks
for one pawn/skill. Inputs are conditional on actual work being done. Each callback
has a unique identity, offset tick, raw XP and learning path; duplicate identities
or unordered callbacks are rejected. Productive output and learning refer to the
same activity: never add a second callback because it also counts as practice.
Predicted levels are not observed capability and do not establish job completion.

| Supported source | Learning boundary | Limits |
| --- | --- | --- |
| Ordinary base-worker recipe without unfinished work | `RecipeCompletionLearning`: native work ticks times recipe learning factor, paid only on completion | An interrupted recipe contributes zero; travel is not work. Custom workers are unknown. |
| Ordinary unfinished recipe | Individual `NormalTracker` callbacks during actual work | Caller supplies native work callback granularity; no second completion payout. |
| Range | `Range` callbacks per completed shot | Direct passion factor only; no global modifier or native saturation. Applied XP consumes the existing range daily budget once. |
| Sparring | `NormalTracker` callbacks per successful melee learning exchange | Current practice verb quotes raw XP using native full-cycle time. Before a practice verb is observed, cadence/XP per exchange is unknown; no hypothetical opponent or safety guarantee is supplied. |
| Idle or active time | Native per-level `Interval` decay | Includes memory traits, disabled skills and tracker restrictions; decay also changes today's XP. |

Normal paths use native saturation once at each callback boundary (strictly past
the threshold), rather than averaging it across a job. Direct range learning
bypasses that path. Stored levels and effective aptitude-adjusted levels remain
separate. The native negative-XP demotion threshold differs from zero progress.
Level caps and practice ceilings are limits, not autonomous targets.

Native decay is read by invoking `Interval` on detached `SkillRecord` instances,
not by maintaining a duplicate trait/level table. The inspected implementation
only invokes negative `Learn`; it changes the detached record, and never enters
the positive-level-gain tale/mote path. The prepared differential checks that
reading mechanics leaves the pawn's real skill unchanged. Arbitrary mod patches
that add side effects or replace these mechanics are not qualified by this contract.

Practice projection is bounded to at most one day and strictly before either native
local midnight or the range global-day reset. Reads during the local reset hour
have no supported forward window. Native fractional local time and a one-tick
margin bound validity. Refresh at the bound, after equipment/trait/health changes,
or when decay makes a previously ceiling-ineligible practice option potentially
eligible. No reset, eligibility recovery, future injury, attack cadence or lifetime
return is invented. Callbacks at decay boundaries are evaluated after decay;
consumers must provide that ordering or reobserve. Unknown input returns an
explicit finding, never guaranteed progress.

The pure tests cover modifiers, distinct XP paths, saturation, budgets, mixed
work/practice, duplicates, interruptions, thresholds, aptitude, decay, ceilings,
unknowns and horizon expiry. Registered `lab/skill-learning` compares bounded
normal/direct learning plus decay with native `SkillRecord` calls across passions
and saturation on prepared detached records. `lab/training-shooting` also compares
actual completed-shot skill/XP against the projection while retaining its real
weapon restoration, dummy and bystander checks. Existing cooking and sparring
cases retain physical work, safety and interruption assertions and now observe actual
native learning callbacks: quoted recipe work XP and sparring verb XP must match
the callback amount, and projected level/progress must match post-callback native
state. The test-only observer never changes callback arguments or issues work.
Registration/compilation is distinct from an executed native pass; nightly runs
supply that evidence.

## Productive cooking in normal selection

Normal food alternatives attach `ProductiveSkillWork` to supported recipe/pawn/station
options. `CompareActivityPlans` calls `ForecastProductiveWork` before admitting a
schedule interval: previous completed callbacks and native decay determine the
stored level and the native work rate for subsequent work. Decay boundaries split
work into segments. Completion-only recipe learning counts actual work ticks once;
travel and waiting earn no XP. Changed durations therefore affect pawn and station
occupancy, deadlines, ingredient use and the downstream needs trajectory. The XP
itself is valued only as capability ([skill progress is capability](#skill-progress-is-capability)).
A small efficiency improvement can break a labor tie only after the existing
higher-tier consequences are compared.

The recipe's work-speed stat at each stored level comes from the generic
[stat-at-level read](#stat-at-level-read), times the observed station speed. Unsupported
pipelines and recipes yield absent learning-dependent rates. Ordinary observed-rate
options remain available, with explicit findings.

Rates hold observed health, equipment, station and environment fixed. In particular,
observed pawn-cell lighting is not a forecast of future bench lighting. Poisoning,
future condition changes and causal skill effects on meal mood are unassessed; speed
is not a safety qualification. The native learning window ends before midnight and
never exceeds a day. Work outside that window cannot claim learning-dependent
capacity; later reviews refresh evidence.

The supported investment demand is scheduled ingestion. Every preceding recipe
whose learning is used to claim a later speedup must have output that actually
supplies food gain in the shared needs trajectory. Unavoidable native batch remainder
stays inventory. Ordinary production and incidental learning need no separate
mandate; the XP they apply is valued as capability like any other. Each produced
lot can serve bounded early/later hunger events, with shared conservation preventing
double consumption. No recipe unlock or current essential-service failure is required.

The Method retains only selected work-time bounds alongside its existing task intent.
Fresh recipe bindings must still support those bounds; missing/slower mechanics,
lost inputs and failed work release the attempt for normal reconsideration. Fresh
needs can retire an ingestion-only investment at a physical work boundary. Unknown
observations do not certify recovery, and in-progress work is not cancelled solely
because hunger temporarily improved. Actual skills and output remain native-owned;
there is no XP ledger or durable training goal.

`productive_learning` in `coupled_selection.predicted_tasks` explains observed-rate
versus conditional duration, callback XP, projected before/after levels, accumulated
gain/decay and native provenance. It is prediction, not completion. Registered
`production/productive-cooking` starts from prepared demand, verifies native per-level
rates against actual stat calls, then observes normal journal-owned cooking, real
learning/output and subsequent work without installing a chosen worker. Pure tests
cover beneficial/costly no-unlock allocations, deadlines, decay, interruption,
fresh-demand retirement and journal/wire preservation. Native execution remains
separate from registration and compilation.

[Assigned capability maintenance](capability-maintenance.md) combines supported scheduled callbacks with decay against actual held-role requirements. Native practice cycle quotes are conditional execution estimates, not guaranteed completed learning or injury-free work.

## Medical work learning (#3632)

Real medical work learns through the same callbacks. A surgery bill's DoBill
allocation carries `ProductiveSkillWork`: the recipe callback pays 0.1 ×
`workSkillLearnFactor` per tick spent (not per work amount) once on completion,
before `Bill_Medical.Notify_IterationCompleted` applies the outcome, so a failed
surgery pays the same. The surgery rate is the doctor's native `workSpeedStat` at
each skill level (`NativeSkillStat.Levels`, #3633), so a level-up inside the work
changes its speed as it would natively; without the per-level curve it falls back to
the observed rate at the current level, where a level change inside the forecast or
work past the learning window leaves the duration unknown and drops that slot's option,
as for cooking.
Game `Recipe_Surgery` workers qualify; mod surgery workers and `Recipe_ExecuteByCut`
stay unknown.

Each tend allocation carries `TendLearning`: `Toils_Tend.FinalizeTend` pays raw XP
once per completed treatment, before and whatever its quality, depending on the
patient (humanlike or animal) and medicine, not on skill. Tend learning never changes
the schedule: the observed tend duration is always kept, and an unprojectable callback
(outside the window, or after the work starts) yields no evidence with a finding.
Neither medical allocation is a `BoundRecipeTask`: only a recipe task bound at its
learning carries `ProductiveWorkTicks` (#3775). `lab/skill-learning-tend` and `lab/skill-learning-surgery` check both native
callbacks against the projection.

## Skill progress is capability

Skill progress is valued as capability, through forecast demand only, for every
skill (#3344). There are no skill targets, floors or hand-set prices per XP. The
tier-2 colony model keeps a stock per pawn and skill (`skill/<pawn>/<skill>`) and
values one XP as the value of one level more over the XP that level takes: the run
with the pawn one stored level higher, less the run as it is, divided by the native
`RequiredXp` of the observed level. The value is what that level raises the colony's
demand for the skill to; a skill at the top level is worth nothing per XP
([forecast value](../architecture/forecast-value.md#tier-2-event-driven-colony-model)).
Tier 1 credits each allocation's learning evidence in the `Investment` term (#3636):
the XP `ForecastSkill` applies (after learning factors and saturation, the gain the
callback adds, not its raw amount) times that value. Every kind of work earns the
same credit per XP. A skill tier 2 gives no value (no stock or no demand) earns
nothing, so XP never wins a comparison on its own. Medicine is the first skill with
a demand (raid tending); medicine the work consumes is charged by kind in the same
term.

[Assigned capability maintenance](capability-maintenance.md) is a separate path:
its `MissingXp` against a held role's requirement stays the requirement route for
those roles and is not priced by this credit.

## Stat-at-level read

`NativeSkillStat` reads a pawn's stat value at each stored level 0-20 without changing
the pawn, a definition or a stat cache. Aptitude applies as native `GetLevel` does. It
copies no curve or formula:

- **Offset path:** a single `SkillNeed_BaseBonus` offset (Cooking's `CookSpeed`) with no
  multiplier after it: the unfinalized value minus the current offset plus native
  `ValueAtLevel`, then native `StatWorker.FinalizeValue`.
- **Factor path:** a single `SkillNeed_BaseBonus` or `SkillNeed_Direct` factor (`valuesPerLevel`
  covering every level): the unfinalized value times the native level value over the current
  one, then `FinalizeValue`. This is exact because only linear multipliers follow the factor:
  `capacityFactors` at observed capacities; inspiration disqualifies.

Everything else must be skill-independent and neutral: exact base worker; nested
`statFactors`/`postProcessStatFactors` with no dependence on the skill; stat parts on the
inspected allowlist (glow, slave, age, overseer); no hediff, inspiration, pawn stuff,
nonneutral trait, life-stage, precept, role or gene factor, stat-relevant conditional
affecter or stat-modifying comp. A pipeline that does not qualify yields no values and a finding.

The pawn biography carries `SkillStatCurves` for MedicalTendQuality, MedicalTendSpeed,
MedicalOperationSpeed and MedicalSurgerySuccessChance against Medicine, and
`SkillStatFindings` ("Stat: reason") for each that does not qualify. Curves hold observed
health, equipment and environment fixed; they are not a forecast of those. Registered
`lab/skill-stat-curve` compares each curve level with the live native stat at that level
(set temporarily by the fixture only) and checks that an anesthetized colonist carries
findings, not curves. Valuing the curves is #3635.
It also carries MeleeDPS against Melee (#3813) and `RangedDefenseDps` against Shooting (#3846): the
primary's ranged DPS times its hit factor at each level's ShootingAccuracyPawn, all zeros without a
ranged primary. Shooting's `SkillLearning.RangeDailyBudget` is the native `RangeTraining.DailyXpBudget`
(0 for other skills), the applied XP a range day may teach.

## Old writer and heuristic audit (#3167)

Selected Methods are the only autonomous source of development, practice and
maintenance in a controlled map. Classes: keep, removed, unsupported-owner (no
selected Method exists yet; documented, not shadowed).

| Path (file:line) | Class | Evidence |
| --- | --- | --- |
| Brain `TrainingTier` / `SparringTier` ladders with `UnlockedTrainingTier` / `UnlockedSparringTier`, and the `RangeWeaponDef` constant (Policy) | removed | No consumer; the native `DefModExtension` XML owns the ladders. |
| Training demand: `ReviewTraining` (`Policy/Training.cs`), `RoundsMilitary.TrainingWorkAsync` | keep | Sizes the range from every pawn `PracticeCapable` for Shooting below the range ceiling (#3846, which also prices it) and the sparring ring from every pawn `PracticeCapable` for Melee below the sparring ceiling, two or more (#3813). Writes no pawn order. |
| `RimGovernorTraining` work-matrix row (`Policy/WorkAssignment.cs:94`) | keep | Capable pawns take priority 3 so the player's work tab stays honest; the roster assigns no training slot. |
| Selected install: `NativePracticeActivities.Job` / `Legal`, `SparringBouts.SelectPair` | keep | Hands-owned exact roster and stand/marker; native drivers keep attacks, budget, stop rules and real-weapon restoration. |
| Native `WorkGiver_TrainRange` / `WorkGiver_Spar` initiation on a map the brain does not control (`RangeTraining.MigratedScope` `:22`; `TrainRangeWorkGiver.cs:20,38`; `SparringWorkGiver.cs:17,27`; `SparringBouts.PlanFor` `:135`, `Free` `:146`, `Form` `:184`) | unsupported-owner | Skipped for every controlled colonist, so no second writer runs under the brain. Uncontrolled maps keep the mod's own player-enabled work type. The prepared `lab/training-*` and `lab/sparring-*` cases drive native mechanics through it; removal first needs those cases moved to selected installs. |
| `RangeTraining.TryChoose` (uncontrolled branch) | removed | Deleted in #3813: practice no longer picks a skill by level or passion; `CanTrain` is the one practice eligibility rule (`SkillLearning.PracticeCapable`). |
| Doctor training by surgery on prisoners (peg-leg cycling, practice surgery, practice slots, `SelectPegCycle`) | removed | Peg-leg training deleted in #3638; practice surgery (#3637), practice slots (#3639) and peg-leg control and reinstall deleted in #3344 ([capability maintenance](capability-maintenance.md#no-practice-surgery-3344)). |
| Productive cooking, food/shelter investment, recipe and bill selection | keep | Selected through `CompareActivityPlans` / `ForecastServiceInvestments`; no local cook or skill winner remains. |
| Training to a ceiling, saved skill target | none found | Ceilings are native feasibility limits only (`RangeTraining.CeilingFor`); nothing is stored. |

## Productive work across the detailed horizon

Research and sowing carry native learning mechanics and per-level rates through their ordinary activity reads.
Research pays Intellectual XP after computing work; sowing pays Plants XP before its work-rate lookup.
The evaluator preserves this ordering, native callback delta and phase, saturation at each callback,
and skill tracker decay after work. Callback runs are compressed for evidence only; arithmetic still
applies each callback separately. Research integrates actual points until its block or the shared project completes;
sowing completes integral cells and discards excess work at each cell just as the native driver does.
The resulting work segments provide threshold dates and per-cell completion dates to continuation.

Normal productive callbacks can cross midnight when the observation includes the next actual tracker
reset. The native read combines the pawn's hash interval, observed update cadence and the tracker's
last reset timestamp; reset precedes decay and follows that tick's work. Observed camera cadence,
health and equipment are held fixed, as other forecast rates are. Range practice retains its separate
existing daily-budget validity bound. No save state or XP ledger is introduced.

Generated research/sowing tests exercise learning, changed work output, conserved cells and carried
terminal XP, including midnight, saturation and callback ordering. Unknown native stat pipelines retain
current-level rates and provenance; crossing into an unobserved rate remains explicit uncertainty.

Construction finishing, floor removal, harvesting, plant cutting, mining and unfinished tailoring
carry their native callbacks through the existing activity reads. Per-target stages preserve differing
plant yields and XP eligibility, discard excess target work, and hold mining speed for each pick timer.
Preparation and ingredient carrying pay no productive XP. Construction uses the native 1.7 factor and
stuff construction-speed multiplier. Roof work, deconstruction, uninstalling and hauling pay no native
skill XP. Recipe and tend completion callbacks precede that tick's skill decay.

Parallel researchers share the remaining project work. Their first common completion callback bounds
all research learning; finalized candidate plans reproject following work from those earned callbacks,
validate the resulting schedule, and price its actual labor. Full terminal learning mechanics carry
stored XP, since-midnight XP, next decay/reset offsets and callback phase into continuation. The range
practice budget keeps its original expiry; rebasing does not manufacture a refreshed budget.

The final research tick follows observed native pawn tick-list order. Once a worker completes the
project, later workers fail that research toil before earning another callback; the final useful work
is capped to the project's remaining points. The read observes pending tick-list registration changes
without writing them. Tailoring's GeneralLaborSpeed is a constant per-level curve when the native stat
dependency graph is skill-independent: Crafting XP does not fabricate a production-speed increase.
The native stat differential checks the five productive speed families at every stored level beside
the four existing medical curves.

Terminal learning retains the nominal hash boundary for decay, separately from the tracker
callback phase. A boundary can be just before the handoff while its callback is still pending;
rebasing preserves that pending boundary and subsequent 200-tick spacing.

The retained native interval counter is read alongside cadence and hash phase. If the observed
camera cadence just changed, its first callback can precede hash alignment; the following
callback can pay a shorter delta. Work, learning, passive decay and terminal rebasing use the
same callback clock, including both transient callbacks.
