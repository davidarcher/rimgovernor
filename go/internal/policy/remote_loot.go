package policy

import (
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LootHold records a safe, forbidden stack demand scoring or a safety
// hold kept forbidden, with the reason, so the review can explain it.
type LootHold struct {
	Thing, Definition string
	Cell              domain.Cell
	Reason            string
}

// lootUnitsPerTrip is a colonist's ordinary carrying capacity in item units.
const lootUnitsPerTrip = 75

// FilterLootReach preserves loot safety and narrows only the
// Allow side: an unsafe stack still forbids, an already allowed stack is not
// re-forbidden by reach, and a safe forbidden stack inside the established
// extent is allowed as before. A safe forbidden stack outside the extent is
// allowed when no safety hold applies, whatever the colony's demand:
// only missing storage headroom and urgent colony work
// keep it forbidden, reported as a hold with an explicit reason
// (RemoteHoldReason). The returned rows are the census the
// safety review should act on.
func FilterLootReach(observed domain.Fact[[]LootItem], r RemoteWorkRequest) (domain.Fact[[]LootItem], []LootHold, error) {
	rows, known := observed.Value()
	if !known {
		return observed, nil, nil
	}
	reasons := make([]string, len(rows))
	for i, row := range rows {
		if !row.SafetyKnown || !row.SafeToHaul || !row.Forbidden || lootInsideExtent(r.Extent, row.Supply.Cell) {
			continue
		}
		reasons[i] = remoteLootReachHold(r)
		if reasons[i] == "" {
			reasons[i] = remoteLootThrottleHold(r, row)
		}
	}
	var kept []LootItem
	var holds []LootHold
	for i, row := range rows {
		if reasons[i] == "" {
			kept = append(kept, row)
			continue
		}
		holds = append(holds, LootHold{Thing: row.Supply.Thing, Definition: row.Supply.Definition, Cell: row.Supply.Cell, Reason: reasons[i]})
	}
	sort.Slice(holds, func(i, j int) bool { return holds[i].Thing < holds[j].Thing })
	return domain.Known(kept), holds, nil
}

// FilterLootReachAdmitted is FilterLootReach for the startup release:
// the scenario's starting stacks are forbidden before the colony has an
// extent, so the safety hold would hold them all. A forbidden stack skips the
// hold on the first review that reads the census (first: no EventLootHistory.Read
// yet), whatever its storage headroom, and while it stays in the previous
// review's pending cohort, so a release of more stacks than one plan holds
// finishes over the following reviews. FilterLootRelease's danger and spawner
// holds run before it and still apply.
func FilterLootReachAdmitted(observed domain.Fact[[]LootItem], r RemoteWorkRequest, previous EventLootHistory, first bool) (domain.Fact[[]LootItem], []LootHold, error) {
	rows, known := observed.Value()
	if !known {
		return observed, nil, nil
	}
	admitted := map[string]bool{}
	for _, row := range previous.Pending {
		admitted[row.Thing] = true
	}
	var exempt, rest []LootItem
	for _, row := range rows {
		if row.Forbidden && (first || admitted[row.Supply.Thing]) {
			exempt = append(exempt, row)
		} else {
			rest = append(rest, row)
		}
	}
	census, holds, err := FilterLootReach(domain.Known(rest), r)
	if err != nil {
		return census, nil, err
	}
	kept, _ := census.Value()
	return domain.Known(append(kept, exempt...)), holds, nil
}

func lootInsideExtent(extent domain.Fact[ColonyExtent], cell domain.Cell) bool {
	e, known := extent.Value()
	if !known {
		return false
	}
	for _, region := range e.Regions {
		for _, c := range region.Cells {
			if c.Cell == cell {
				return true
			}
		}
	}
	return false
}

// remoteLootReachHold returns the empty string when no safety hold applies.
// The native safety verdict is this candidate's eligibility and route
// evidence: the census only reports SafeToHaul after walking a colonist's
// route. Distance is the supply planner's price, not a hold.
func remoteLootReachHold(r RemoteWorkRequest) string {
	return remoteThreatHold(r.Safety)
}

// remoteLootThrottleHold is the per-stack throttle that outlives the demand
// gate: no known storage headroom for the stack, or urgent colony work.
func remoteLootThrottleHold(r RemoteWorkRequest, row LootItem) string {
	if units, known := row.StorageHeadroom.Value(); !known || units <= 0 {
		return RemoteHoldMissingStorage
	}
	if r.Competition.UrgentPriority > 0 {
		return RemoteHoldUrgentWork
	}
	return ""
}

// lootCandidate is a stack as a supply candidate: its census count, hauled to
// the storage headroom the census reports, no labor beyond the haul.
func lootCandidate(row LootItem) SupplyCandidate {
	return SourceCandidate(CandidateLoot, row.Supply.Thing, domain.Known(0.0), row.PathLength, true, lootUnitsPerTrip,
		SourceYield(ResourceKey{Def: Resource(row.Supply.Definition)}, row.Count, 0, row.StorageHeadroom))
}

// LootContext is the remote request the loot filter runs against: safety and
// the derived extent from the review facts, and the urgent work competing for
// the colonists.
func LootContext(f RoundsFacts) (RemoteWorkRequest, error) {
	extent, err := DeriveColonyExtent(ColonyExtentRequest{Bounds: f.MapBounds, Construction: f.CurrentConstruction, Claims: f.ConstructionClaims, Home: f.HomeCoverage})
	return RemoteWorkRequest{Safety: LootSafety(f), Extent: extent, Competition: RemoteCompetition(f)}, err
}

// LootSafety assembles the outdoor safety from the review facts. Unknown
// hostiles stay unknown.
func LootSafety(f RoundsFacts) RemoteSafety {
	r := RemoteSafety{Hazard: OutdoorHazardsOf(f), DashOpen: f.DashOpen()}
	if hostiles, known := f.Hostiles.Value(); known {
		r.Threat = domain.Known(hostiles > 0)
	}
	return r
}
