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

The projection is bounded to at most one day and strictly before either native
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
occupancy, deadlines, ingredient use and the downstream needs trajectory. XP and
terminal skill have no utility score. A small efficiency improvement can break a
labor tie only after the existing higher-tier consequences are compared.

Native `CookSpeed` currently depends on a Cooking skill offset before a native
postprocessing curve. The read substitutes native `SkillNeed_BaseBonus.ValueAtLevel`
values and invokes native `StatWorker.FinalizeValue`; it copies neither curve nor
skill-speed formula. It changes no pawn, definition or skill and does not populate
counterfactual stat-cache entries. This qualification requires exact base worker and
skill-need types, neutral prefinal multipliers and inspected skill-independent
postprocessing parts. Hediffs, nonneutral multipliers, conditional modifier hooks,
custom stat pipelines and unsupported recipes yield absent learning-dependent rates.
Ordinary observed-rate options remain available, with explicit findings.

Rates hold observed health, equipment, station and environment fixed. In particular,
observed pawn-cell lighting is not a forecast of future bench lighting. Poisoning,
future condition changes and causal skill effects on meal mood are unassessed; speed
is not a safety qualification. The native learning window ends before midnight and
never exceeds a day. Work outside that window cannot claim learning-dependent
capacity; later reviews refresh evidence.

The supported investment demand is scheduled ingestion. Every preceding recipe
whose learning is used to claim a later speedup must have output that actually
supplies food gain in the shared needs trajectory. Unavoidable native batch remainder
stays inventory. Ordinary production and terminal incidental learning need no separate
mandate; speculative reserve learning receives no additional value. Each produced
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
