# Disaster planning

[Documentation](../../README.md) · [Issues](https://github.com/davidarcher/rimgovernor/issues)

The brain rounds track environmental disruption inside the shared concern journal.
Native condition identities start an episode. `ColonyFactsSnapshot.environment`
is the game-condition census: every condition affecting the map with its
definition, implementation class, label, whether it is permanent and, for a
timed condition, the native remaining ticks. Remaining time is planning evidence
only; an episode ends when the condition is no longer observed, never when the
count runs out. A condition starting or ending under a running clock
invalidates the `colony` fact family (see the clock contract), because such
events arrive as non-stopping letters. The food, production,
sleeping, shelter, temperature, cooking, power and storage gates determine affected
services; a complete native recovery census supplies infrastructure evidence.
Named conditions also shape ordinary reviews: a solar flare suspends power
building, cooks the warm stock ahead and treats turrets as absent, an eclipse
zeroes solar output and lights outdoor work cells, and a psychic drone widens
mood entry for the pawns it affects ([upkeep contracts](upkeep-contracts.md),
[power contracts](power-contracts.md), [mood relief](mood-control.md)).

Crop conditions price into the food plan as crop-channel risk and lead
(`policy.CropChannels`, [supply model](../architecture/supply-model.md#crop-risk-and-pause)):
an eclipse, volcanic winter or cold snap with a remaining-duration read pushes
every crop's lead back by those days, and toxic fallout discounts a field by the
share of its cells under open sky. Fallout, blight and frost never name a
disaster episode; they stay food-plan inputs.

An observed Zzztt letter also starts recovery without a game condition.
`DevelopmentFacts.short_circuit` (`none_seen`, or `seen` with its tick) reports the latest matching map-local
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
tracked damage. Fuel shortages use the native target and enter at or below the building's own
native auto-refuel fraction of that target (`Props.autoRefuelPercent`, read per building,
0.3 by default). Forbidden or burning buildings are excluded from new work; their
exclusion cannot clear previously tracked damage.

The review records ordered refuel, breakdown and repair needs. It ranks nothing: concerns carry no priority, and a disrupted service is simply
unmet. The disaster family no longer selects or dispatches any of that work (milestone 5 of
`redefine-habitat-requirements`): each lost service is compared and ordered by the family that
owns it. `power/basic` refuels a lost producer, acquires its fuel first when the colony holds
none, or lets a fix restore it ([power contracts](power-contracts.md#restoration)), and
`home-upkeep/repairs` repairs and fixes damaged or broken-down buildings
([upkeep contracts](upkeep-contracts.md#repairs-cleaning)). While its Need is unmet
`disaster/services` holds `services_by_owning_families`, naming both, and closes only when the
disaster history's next finding shows every tracked service restored. Player cancellation,
resource reservations and admission checks still apply to the owning families' orders. A known
roof-sensitive hazard independently keeps `disaster/sheltering`
unmet while a move is owed, and keeps `disaster/services` unmet, including when buildings are intact or another service is unknown.

`disaster/sheltering` still produces area-correction Methods. During a roof-sensitive hazard an
observed restriction outside the known roofed areas is moved into an existing wholly roofed
allowed area (at most two per pawn); each move retains the prior restriction and never
authorizes widening player access. Exhausted refuge methods do not fall through to exposed
service work. Unknown restrictions, missing roster identities and unknown availability
preserve explicit blockers. Refuge identities include the prior area and a 600-tick lease
window and use ordinary work-settings and husbandry actions.

There is no retained recovery selection: the review stores no candidate record, and a journal
written before schema 74 that carried one is replaced on load
([persistence](persistence-contracts.md)). Applied intent does not prove recovery. Observed
useful service jobs retain their responsibility as hit points or fuel change; unknown jobs hold
replacement. See [Method continuation](method-continuation.md) for reconsideration and release.

Manual control suspends the recovery concern while retaining observation
history; the episode survives a resume. Colony/load/map replacement or tick rewind
resets it.
A restored episode stays closed through unrelated later shortages and a newly
observed condition starts a fresh episode. Routine events make no model calls.

`ColonyFactsSnapshot.recovery` contains a same-context bounded census of player
buildings, wholly roofed visible allowed areas, and colonist area restrictions.
Reads do not acquire area leases, alter restrictions, or issue work. Partial or
unavailable censuses remain unknown. See acceptance.

Survival services have explicit restored or deficit states. Food alone awaits the first food plan; that state prevents a recovery claim. A colony without an indoor bed has an unmet temperature service. During cold snaps or heat waves that sleeping state cannot clear sheltering; the family requires an indoor measurement. Before the first emergency census, sheltering likewise awaits that census. Warmth window planning reads total body measurements, one window per pawn; the cold estimator names finite, unbounded and unmodelled hazards. Raid harm reads observed raid points and defense capacity directly; zero capacity cannot establish a calibrated fight ratio. Hunting admission uses an observed integer capacity, including zero when no hunter is available.
Safe-area review status is awaiting-review before a review in this world, edits when area differences are queued, and clear after commit or an unchanged review. An awaiting review cannot establish that the safe-area Need is met.
