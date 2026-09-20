package policy

import (
	"errors"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// LootReadiness is the loot census's own readiness evidence for the resource
// reach stages (#520): free hauling colonists and storyteller quietness.
// Absent facts leave reach at its base stage, never widen it.
type LootReadiness struct {
	FreeHaulers      domain.Fact[int64]
	StorytellerQuiet domain.Fact[bool]
}

// LootHold records a safe, forbidden stack the reach stage or demand scoring
// kept forbidden, with the reason, so the review can explain it.
type LootHold struct {
	Thing, Definition string
	Cell              domain.Cell
	Reason            string
}

// lootUnitsPerTrip is a colonist's ordinary carrying capacity in item units.
const lootUnitsPerTrip = 75

// FilterLootReach keeps #336's safety semantics untouched and narrows only the
// Allow side: an unsafe stack still forbids, an already allowed stack is not
// re-forbidden by reach, and a safe forbidden stack inside the established
// extent is allowed as before. A safe forbidden stack outside the extent is
// allowed only when the reach stage admits its cell and it scores against
// unmet demand; otherwise it stays forbidden and is reported as a hold with
// an explicit reason (RemoteHoldReason). The returned rows are the census the
// safety review should act on.
func FilterLootReach(observed domain.Fact[[]LootItem], r RemoteWorkRequest) (domain.Fact[[]LootItem], []LootHold, error) {
	rows, known := observed.Value()
	if !known {
		return observed, nil, nil
	}
	if len(rows) > 4096 {
		return domain.Unknown[[]LootItem](), nil, errors.New("event loot census exceeds bound")
	}
	var kept []LootItem
	var holds []LootHold
	for _, row := range rows {
		if !row.SafetyKnown || !row.SafeToHaul || !row.Forbidden || lootInsideExtent(r.Reach.Extent, row.Supply.Cell) {
			kept = append(kept, row)
			continue
		}
		reason, err := remoteLootHold(r, row)
		if err != nil {
			return domain.Unknown[[]LootItem](), nil, err
		}
		if reason == "" {
			kept = append(kept, row)
			continue
		}
		holds = append(holds, LootHold{Thing: row.Supply.Thing, Definition: row.Supply.Definition, Cell: row.Supply.Cell, Reason: reason})
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

// remoteLootHold returns the empty string when the stack may be allowed. The
// native safety verdict is this candidate's eligibility and route evidence:
// the census only reports SafeToHaul after walking a colonist's route. A
// known threat is reported before the reach stage it collapses.
func remoteLootHold(r RemoteWorkRequest, row LootItem) (string, error) {
	if reason := remoteThreatHold(r.Reach); reason != "" {
		return reason, nil
	}
	filter := FilterResourceReach(r.Reach, ResourceReachCandidate{Cell: row.Supply.Cell, Eligible: domain.Known(true), RouteObservedPassable: domain.Known(true)})
	if !filter.Allowed {
		return RemoteHoldReason(RemoteLoot, filter.Reason), nil
	}
	candidate := AcquisitionCandidate{
		ID: row.Supply.Definition, Kind: AcquisitionLoot,
		Yields:       []AcquisitionYield{{ResourceQuantity: ResourceQuantity{Key: ResourceKey{Def: Resource(row.Supply.Definition)}, Count: row.Count}, Headroom: row.StorageHeadroom}},
		PathDistance: row.PathLength, Labor: domain.Known(0.0), NeedsHaul: true, UnitsPerTrip: lootUnitsPerTrip,
	}
	score, err := ScoreResourceCandidate(r.Demand, candidate, r.Competition)
	if err != nil {
		return "", err
	}
	if score.Score <= 0 {
		return RemoteHoldReason(RemoteLoot, "demand:"+score.Hold), nil
	}
	return "", nil
}

// LootDemand builds the demand remote loot scores against: the effective
// stock targets, the operator's reserve floors and the current usable stock.
func LootDemand(p RoutinePolicy, f RoutineFacts) (domain.Fact[[]ResourceDemand], error) {
	targets, err := p.EffectiveResourceTargets(f.Resources, f.ResourceNeeds)
	if err != nil {
		return domain.Unknown[[]ResourceDemand](), err
	}
	in := ResourceDemandInput{EconomicFloors: map[string]int64{}}
	for resource, count := range targets {
		if count > 0 {
			in.Targets = append(in.Targets, ResourceDemand{Key: ResourceKey{Def: resource}, Count: count, Priority: 2})
		}
	}
	for resource, count := range p.ResourceReserves {
		if count > 0 {
			in.EconomicFloors[string(resource)] = count
		}
	}
	if stock, known := f.Resources.Value(); known {
		rows := make([]ResourceQuantity, 0, len(stock))
		for _, amount := range stock {
			if amount.Count > 0 {
				rows = append(rows, ResourceQuantity{Key: ResourceKey{Def: amount.Resource}, Count: amount.Count})
			}
		}
		in.Stock = domain.Known(rows)
	}
	return BuildResourceDemand(in)
}

// LootReach assembles the reach readiness for the loot filter from the review
// facts: threat, defense and raid points from the colony census, hauling and
// storyteller quietness from the loot census's own readiness, and storage
// headroom from the census's accepting-storage figures. Unknown stays unknown.
func LootReach(f RoutineFacts, bounds domain.Fact[Bounds], extent domain.Fact[ColonyExtent]) ResourceReachRequest {
	r := ResourceReachRequest{Extent: extent, Bounds: bounds, RaidPoints: f.RaidPoints, Armed: f.Armed,
		FreeHaulers: f.LootReadiness.FreeHaulers, StorytellerQuiet: f.LootReadiness.StorytellerQuiet}
	if hostiles, known := f.Hostiles.Value(); known {
		r.Threat = domain.Known(hostiles > 0)
	}
	if rows, known := f.EventLoot.Value(); known {
		headroom := int64(0)
		complete := true
		for _, row := range rows {
			units, ok := row.StorageHeadroom.Value()
			if !ok {
				complete = false
				break
			}
			headroom = max(headroom, units)
		}
		if complete {
			r.StorageHeadroom = domain.Known(headroom)
		}
	}
	return r
}
