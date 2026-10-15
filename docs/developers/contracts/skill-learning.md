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
supply that evidence. No selector, target-floor policy or training writer changes.
