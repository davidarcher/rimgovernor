package policy

import (
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
// allowed when the reach stage admits its cell, whatever the colony's demand
// (#2299, epic #2291): only missing storage headroom and urgent colony work
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
		if !row.SafetyKnown || !row.SafeToHaul || !row.Forbidden || lootInsideExtent(r.Reach.Extent, row.Supply.Cell) {
			continue
		}
		reasons[i] = remoteLootReachHold(r, row)
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

// FilterLootReachAdmitted is FilterLootReach for the startup release (#2188):
// the scenario's starting stacks are forbidden before the colony has an
// extent, so the reach stage would hold them all. A forbidden stack skips the
// stage on the world's first review (first) and while it stays in the previous
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

// remoteLootReachHold returns the empty string when the stack passes the
// reach stage. The native safety verdict is this candidate's eligibility and
// route evidence: the census only reports SafeToHaul after walking a
// colonist's route. A known threat is reported before the reach stage it
// collapses.
func remoteLootReachHold(r RemoteWorkRequest, row LootItem) string {
	if reason := remoteThreatHold(r.Reach); reason != "" {
		return reason
	}
	filter := FilterResourceReach(r.Reach, ResourceReachCandidate{Cell: row.Supply.Cell, Eligible: domain.Known(true), RouteObservedPassable: domain.Known(true)})
	if !filter.Allowed {
		return RemoteHoldReason(RemoteLoot, filter.Reason)
	}
	return ""
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

// LootDemand builds the demand remote loot scores against: the effective
// stock targets (which carry the derived resource needs) and the current
// usable stock.
func LootDemand(p RoundsPolicy, f RoundsFacts) (domain.Fact[[]ResourceDemand], error) {
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
func LootReach(f RoundsFacts, bounds domain.Fact[Bounds], extent domain.Fact[ColonyExtent]) ResourceReachRequest {
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
