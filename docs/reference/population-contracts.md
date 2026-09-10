# Population commitments

[Documentation](../README.md)

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

`home/population` reads human pawn custody, recruitment eligibility, current exclusive
interaction, resistance, food need, tending need, bed and owned bed. Its only settings
are the discovered native recruitment and maintain-only definitions. A write requires
the exact prior exclusive interaction and a living current-map colony prisoner.
Individual setting changes stop managed recruitment until new explicit direction.
Native faction admission, resistance and recruitment probability are never written.

`home/order` capture uses the installed game's capture eligibility, manipulation,
reservation and bed checks, followed by its ordinary Capture job. Non-hostile capture
is explicitly unsupported because it changes faction relations. Rescue remains the
existing ordinary rescue path. Unknown prerequisites block new commitments.

An issued order is not successful custody. The population goal observes actual
prisoner status and bed occupancy; care requires observed food and tending state.
Recruitment requires a native free-colonist read. Integration requires an owned bed,
active work allocation, equipment (or native incapability of violence), and current
care needs met. Shared work/shelter methods handle new colonists, and the population
method can allocate an available eligible weapon without replacing existing gear.
Completed admission never authorizes recapturing a pawn who later leaves.

Pending orders retain normal identity, direction, uncertainty and cancellation guards.
Population methods do not automatically replay custody orders. A ten-day native-tick
watchdog bounds stalled issued work. Patient-care, recruitment and integration are
separate observed phases; voluntary joining after rescue remains RimWorld's decision.

The optional `PopulationFixture` build prepares test-only starting conditions and is
excluded from production and model execution. Docker evidence distinguishes this
fixture's prerequisites from the ordinary pawn outcomes asserted afterward.
