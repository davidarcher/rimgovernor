package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// The acquisition method catalog (#728 step 2): every way the colony can
// obtain a resource is an AcquisitionCandidate of one kind, ranked by
// RankResourceCandidates against the demand, instead of each goal
// hardcoding its own order (produce before mine, forage before hunt).
// Loot, salvage and mining were the first kinds; these add the rest.
const (
	AcquisitionProduce   AcquisitionKind = "produce"    // a bench bill
	AcquisitionDeepDrill AcquisitionKind = "deep_drill" // a drill over a lump
	AcquisitionChop      AcquisitionKind = "chop"       // a tree
	AcquisitionHarvest   AcquisitionKind = "harvest"    // a wild plant
	AcquisitionHunt      AcquisitionKind = "hunt"       // an animal
	AcquisitionTrade     AcquisitionKind = "trade"      // a trader present
)

// AcquisitionLaborPerUnit is each kind's estimated pawn work ticks per unit
// yielded: a planning prior for the kinds whose per-unit work native does
// not report, so a bill and a deposit covering the same deficit compare.
// Measured distance, trips and risk refine a candidate on top of it.
var AcquisitionLaborPerUnit = map[AcquisitionKind]float64{
	AcquisitionHarvest:   10,
	AcquisitionChop:      15,
	AcquisitionMining:    20,
	AcquisitionSalvage:   25,
	AcquisitionLoot:      5,
	AcquisitionHunt:      30,
	AcquisitionProduce:   60,
	AcquisitionDeepDrill: 100,
	AcquisitionTrade:     0,
}

// acquisitionUnitsPerTrip is one pawn's haul of a stackable resource when
// no carry capacity was observed.
const acquisitionUnitsPerTrip = 75

func catalogLabor(kind AcquisitionKind, units int64) domain.Fact[float64] {
	return domain.Known(AcquisitionLaborPerUnit[kind] * float64(max(units, 1)))
}

// ResourceDeficitDemand is the one-row demand a MaintainResource floor
// ranks candidates against.
func ResourceDeficitDemand(resource Resource, deficit int64) domain.Fact[[]ResourceDemand] {
	if deficit <= 0 {
		return domain.Known([]ResourceDemand{})
	}
	return domain.Known([]ResourceDemand{{Key: ResourceKey{Def: resource}, Count: deficit, Priority: 1}})
}

// MineCandidates are the catalog rows of selected native mine sources;
// headroom is the storage accepting the output.
func MineCandidates(resource Resource, sources []ResourceSource, headroom domain.Fact[int64]) []AcquisitionCandidate {
	var out []AcquisitionCandidate
	for _, s := range sources {
		if s.Method != ResourceSourceMine || s.Yield <= 0 {
			continue
		}
		out = append(out, AcquisitionCandidate{
			ID: s.ThingID, Kind: AcquisitionMining,
			Yields:       []AcquisitionYield{{ResourceQuantity: ResourceQuantity{Key: ResourceKey{Def: resource}, Count: s.Yield}, Headroom: headroom}},
			PathDistance: domain.Known(s.Distance), Labor: catalogLabor(AcquisitionMining, s.Yield),
			NeedsHaul: true, UnitsPerTrip: acquisitionUnitsPerTrip,
		})
	}
	return out
}

// ProduceCandidate is the catalog row of a chosen production bill covering
// units: the product drops at the bench, so no haul is charged.
func ProduceCandidate(m ResourceMethod, units int64) (AcquisitionCandidate, bool) {
	if m.Kind != ResourceMethodProduce || units <= 0 {
		return AcquisitionCandidate{}, false
	}
	return AcquisitionCandidate{
		ID: m.Bench + "/" + m.Recipe, Kind: AcquisitionProduce,
		Yields:       []AcquisitionYield{{ResourceQuantity: ResourceQuantity{Key: ResourceKey{Def: m.Resource}, Count: units}}},
		PathDistance: domain.Known(0.0), Labor: catalogLabor(AcquisitionProduce, units),
	}, true
}

// AcquisitionSourceCandidates are the catalog rows of the colony-facts
// acquisition census (trees, wild plants, animals) yielding resource: a
// tree is chop, a hunt is hunt, anything else harvest. A hunt's revenge
// chance is its risk: labor scales by 1+2*chance. Distance is straight
// from home, the colony's reference cell.
func AcquisitionSourceCandidates(resource Resource, sources []AcquisitionSource, home domain.Cell, headroom domain.Fact[int64]) []AcquisitionCandidate {
	var out []AcquisitionCandidate
	for _, s := range sources {
		units := int64(math.Round(s.Yield))
		if Resource(s.Resource) != resource || units <= 0 {
			continue
		}
		kind, risk := AcquisitionHarvest, 0.0
		switch {
		case s.Hunt:
			kind, risk = AcquisitionHunt, math.Min(1, math.Max(0, s.RevengeChance))
		case s.Tree:
			kind = AcquisitionChop
		}
		labor, _ := catalogLabor(kind, units).Value()
		out = append(out, AcquisitionCandidate{
			ID: s.ID, Kind: kind,
			Yields:       []AcquisitionYield{{ResourceQuantity: ResourceQuantity{Key: ResourceKey{Def: resource}, Count: units}, Headroom: headroom}},
			PathDistance: domain.Known(math.Hypot(float64(s.Cell.X-home.X), float64(s.Cell.Z-home.Z))),
			Labor:        domain.Known(labor * (1 + 2*risk)),
			NeedsHaul:    true, UnitsPerTrip: acquisitionUnitsPerTrip,
		})
	}
	return out
}

// maxCatalogSelection bounds one acquisition method, matching
// SelectResourceSources' native selection cap.
const maxCatalogSelection = 8

// SelectCatalogAcquisition picks MaintainResource's chop, harvest and hunt
// sources for resource (#728): every undesignated, unheld census row
// yielding it is a catalog candidate, ranked by RankResourceCandidates
// against need less the yield already designated, and taken best first
// until the rest of the need is covered, at most huntSlots hunts and
// maxCatalogSelection rows.
func SelectCatalogAcquisition(rows []AcquisitionSource, resource Resource, need int64, home domain.Cell, held map[string]bool, huntSlots int) ([]AcquisitionSource, error) {
	byID := map[string]AcquisitionSource{}
	var open []AcquisitionSource
	for _, row := range rows {
		if Resource(row.Resource) != resource {
			continue
		}
		if row.Designated {
			need -= int64(math.Round(row.Yield))
			continue
		}
		if held[row.ID] || row.Hunt && huntSlots <= 0 {
			continue
		}
		byID[row.ID] = row
		open = append(open, row)
	}
	if need <= 0 || len(open) == 0 {
		return nil, nil
	}
	ranked, err := RankResourceCandidates(ResourceDeficitDemand(resource, need), AcquisitionSourceCandidates(resource, open, home, domain.Known(need)), AcquisitionCompetition{})
	if err != nil {
		return nil, err
	}
	var out []AcquisitionSource
	for _, s := range ranked {
		row := byID[s.ID]
		if row.Hunt {
			if huntSlots <= 0 {
				continue
			}
			huntSlots--
		}
		out = append(out, row)
		need -= int64(math.Round(row.Yield))
		if need <= 0 || len(out) == maxCatalogSelection {
			break
		}
	}
	return out, nil
}
