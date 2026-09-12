# Disaster planning

[Documentation](../../README.md) · [Routine planning backlog](../../BACKLOG.md)

Go routine reviews track environmental disruption inside the shared goal journal.
Native condition identities start an episode. The existing food, production,
sleeping, shelter, temperature, cooking, power and storage gates determine affected
services; a complete native recovery census supplies infrastructure evidence.

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
admission checks still apply. `RecoverDisasterServices` records infrastructure need;
its native job dispatch is unavailable until the shared action family is connected.

Manual invalidates pending work while retaining observation history. New player
direction retains the episode; colony/load/map replacement or tick rewind resets it.
A restored episode stays closed through unrelated later shortages and a newly
observed condition starts a fresh episode. Routine events make no model calls.

`ColonyFactsSnapshot.recovery` contains a same-context bounded census of player
buildings, wholly roofed visible allowed areas, and colonist area restrictions.
Reads do not acquire area leases, alter restrictions, or issue work. Partial or
unavailable censuses remain unknown. See [acceptance](../testing/disaster-planning.md).
