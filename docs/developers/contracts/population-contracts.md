# Population commitments

[Documentation](../../README.md)

`SetPopulationPolicy` records an explicit maximum population and minimum food reserve
in days. It grants no permission to capture or recruit an individual.
`SetPopulationDecision` records an exact observed pawn ID and one of `rescue`,
`capture`, `recruit` or `ignore`. Ignore cancels future population work; it does not
release a prisoner or undo a native order.

Each decision uses a `Population-<pawn ID>` ColonyGoal and ordinary Hands actions.
The controller counts living free player colonists as admitted population. Guests,
prisoners and accepted candidates consume reserved capacity but remain distinct from
admitted colonists. Shared food and shelter methods can provision future capacity.
Custody orders require the policy food reserve, spare colonist beds and available
assigned doctors and wardens. Native capture/rescue previews separately require an
eligible worker, reachable target and suitable available custody bed.

`rimgovernor/observations_read_population` reads human pawn custody, recruitment
eligibility, current exclusive interaction, resistance, time held as a prisoner
(`prisoner_ticks`, the `TimeAsPrisoner` record), food need, bed and owned bed,
and lists the installed exclusive interactions `SetPrisonerInteraction` accepts:
`AttemptRecruit`, `MaintainOnly`, `ReduceResistance`, `Release`, and `Enslave` and
`Convert` while Ideology is active. Execution and non-exclusive toggles are player-only.
A write requires the exact prior prisoner settings token and a living current-map
colony prisoner; native gates (recruitable, wild man, classic ideology mode) refuse
ineligible modes. Routine planning (`MaintainPopulation`) proposes `AttemptRecruit`
for any recruitable prisoner not already set to it and, only once the operator sets
`--routine-prisoner-release-after-days N`, `Release` for a prisoner held at least
`N` days whom the colony cannot turn (recruit resistance still above zero, or never
recruitable) while the colony food runway is below its routine target
(`RoutinePolicy.FoodTargetDays`); a colony at or above its target keeps feeding the
prisoner, and an unknown resistance, held-time or food fact never authorizes a
release. Release takes precedence over recruit for the same prisoner. Every other
mode is an explicit order. Native faction admission, resistance and recruitment
probability are never written.

The `population-joiner` routine family (on in the autonomous default; selected by
`RIMGOVERNOR_ROUTINE_FAMILIES` like every other family) lets the same
goal answer joiner quests from population capacity. The routine review reads the
visible quest census and accepts a not-yet-accepted `ThreatReward_*_Joiner` offer
(a refugee chased by a threat; the native `CanAcceptQuest` verdict is re-read at
dispatch) through `AcceptQuest` only when the player has set a population policy
and living admitted colonists plus guests and prisoners are below its maximum, the
food runway is at or above its reserve days and an unowned humanlike, non-medical,
non-prisoner bed reads back. Without a policy, or without room, the offer is left
to expire; nothing is ever rejected natively, and a reward-choice offer takes the
game's first option. Pending current-map `WandererJoins` letters are read through
the typed colony census (`joiner_letters`) with their letter ID, pawn, expiry
and snapshot token. The same capacity policy admits one letter answer at a time
through `AnswerDialog.joiner_letter_token`. Native rechecks the exact letter,
quest, pawn, map, expiry and option under authority and runs its ordinary Accept
option. Completion requires the offered pawn to be a living spawned free colonist
on that map; closing the letter alone is insufficient. Expired, changed and
unsupported offers are never answered. Without known capacity, letters expire
through their own native quest timeout.

Progress observation of a prisoner order reads the pawn's actual custody state, not
only the setting: `PrisonerEffect.outcome` is `held`, `recruited`, `enslaved`,
`converted`, `released`, `escaped` or `died`. While held, the order is complete as
long as its setting is still set. Once the pawn leaves custody, the order is complete
only when the outcome is the one its mode pursues (recruit→recruited,
release→released, enslave→enslaved, convert→converted) and unsuccessful otherwise;
a pawn that is no longer observable anywhere stays unknown.

`home/order` capture uses the installed game's capture eligibility, manipulation,
reservation and bed checks, followed by its ordinary Capture job. Non-hostile capture
is explicitly unsupported because it changes faction relations. Rescue remains the
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
excluded from production and model execution. Docker evidence distinguishes this
fixture's prerequisites from the ordinary pawn outcomes asserted afterward.
