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
// allowed when no safety hold applies, whatever the colony's demand,
// storage headroom or competing work: Allow only clears a flag and dispatches
// no pawn, so the storage and urgent-work holds of dispatched remote work do
// not apply. A safety hold is reported with an explicit reason. The returned
// rows are the census the safety review should act on.
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
