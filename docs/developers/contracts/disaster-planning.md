# Disaster planning

[Documentation](../../README.md) · [Issues](https://github.com/davidarcher/rimgovernor/issues)

Go routine reviews track environmental disruption inside the shared goal journal.
Native condition identities start an episode. `ColonyFactsSnapshot.environment`
is the game-condition census: every condition affecting the map with its
definition, implementation class, label, whether it is permanent and, for a
timed condition, the native remaining ticks. Remaining time is planning evidence
only; an episode ends when the condition is no longer observed, never when the
count runs out. A condition starting or ending under a running clock window
invalidates the `colony` fact family (see the clock contract), because such
events arrive as non-stopping letters. The existing food, production,
sleeping, shelter, temperature, cooking, power and storage gates determine affected
services; a complete native recovery census supplies infrastructure evidence.
Named conditions also shape ordinary reviews: a solar flare suspends power
building, cooks the warm stock ahead and treats turrets as absent, an eclipse
zeroes solar output and lights outdoor work cells, and a psychic drone widens
mood entry for the pawns it affects ([upkeep contracts](upkeep-contracts.md),
[power contracts](power-contracts.md), [mood relief](mood-control.md); #408).

An observed Zzztt letter also starts recovery without a game condition.
`DevelopmentFacts.short_circuit_tick` reports the latest matching map-local
letter in the native active stack or archive, using the game's translated label.
The durable episode records the processed tick so dismissed or repeatedly read
letters do not reopen it. Damaged buildings enter history even while burning;
the letter alone never certifies damage or repair.

An episode is `disrupted` while conditions and measured deficits remain,
`temporary_survival` while conditions remain with every service recovered,
`recovering` after conditions end with measured deficits, and `restored` only when
all services recover. Missing observations preserve uncertainty. A measured deficit
can establish disruption while another service remains unknown; unknown services
always prevent restored or temporary-survival claims. Missing environmental reads
retain the episode as `unknown` until conditions are observed again.

History retains affected services and exact damaged-building identities. An expired
condition, a missing building, a receipt or elapsed ticks cannot certify repair.
Observed full native hit points and cleared breakdown state establish recovery of
tracked damage. Fuel shortages use the native target and enter below one quarter
of that target. Forbidden or burning buildings are excluded from new work; their
exclusion cannot clear previously tracked damage.

The review records ordered refuel, breakdown and repair needs. It promotes affected
service goals to priority 2, including wood when cooking or temperature is disrupted.
Existing emergency priorities, player cancellation, resource reservations and
admission checks still apply. A known roof-sensitive hazard independently keeps
`RecoverDisasterServices` at priority 2, including when buildings are intact or
another service is unknown.

The durable `Recovery` review records at most eight native admission candidates.
Available workers are ordered by exact pawn identity. During a roof-sensitive
hazard, an observed restriction outside the known roofed areas produces refuge
candidates first, using at most two existing areas per pawn. Each candidate retains
the prior restriction; it never authorizes widening player access. Exhausted refuge
methods do not fall through to exposed service work. Unknown restrictions, missing
roster identities and unknown availability preserve explicit blockers.

With exposure protection satisfied, candidate pairs use the ordered refuel,
breakdown and repair needs. Method identities include pawn/target and observed
hit points or fuel; refuge identities include the prior area and a 600-tick lease
window. The shared goal epoch supplies used methods, including retired plans.
Refreshing a proposal does not count as attempting it. Saved typed inputs reproduce
the candidates on load, and cancelled or emergency-suspended goals receive no new
proposals.

Candidates require fresh native preview of pawn eligibility, reachability,
reservations, supplies, area safety and non-widening restrictions. Native job and
area-lease dispatch remain unavailable until the shared action family is connected.
Proposals do not allocate resources, alter areas, issue jobs or prove recovery.

Manual clears candidates and suspends the recovery goal while retaining observation
history; the episode survives a resume. Colony/load/map replacement or tick rewind
resets it.
A restored episode stays closed through unrelated later shortages and a newly
observed condition starts a fresh episode. Routine events make no model calls.

`ColonyFactsSnapshot.recovery` contains a same-context bounded census of player
buildings, wholly roofed visible allowed areas, and colonist area restrictions.
Reads do not acquire area leases, alter restrictions, or issue work. Partial or
unavailable censuses remain unknown. See acceptance.
