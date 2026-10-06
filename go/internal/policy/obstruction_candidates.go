package policy

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// ObstructionPricing is what the clearance a reconcile owes is priced from
// (#2270, epic #2241): the yield of removing a foreign thing is a resource
// candidate like a tree's wood or an animal's meat, so clearing credits a
// resource instead of nothing.
type ObstructionPricing struct {
	// Salvage is the native cost-list yield of the census rows, by entity id.
	Salvage map[string]SalvageEvidence
	// Crops price a cut plant: a felled tree yields its harvest.
	Crops []CropChoice
	// Remote is the ruin the review's remote salvage path already admitted
	// (ReviewClearanceHolds): that path prices it, so it is not priced twice.
	Remote string
}

// ObstructionCandidates are the catalog rows of the foreign things ops clear
// that yield resource. A deconstruction credits the native cost-list yield
// (CandidateSalvage), a cut plant its harvest when the harvest destroys the
// plant (CandidateChop), a hauled item its own stack (CandidateLoot). A thing
// is one row however many ops name it, and the remote path's ruin is none.
// Unknown yields price nothing, never zero.
func ObstructionCandidates(resource Resource, ops []Operation, pricing ObstructionPricing, home domain.Cell, headroom domain.Fact[int64]) []SupplyCandidate {
	var out []SupplyCandidate
	seen := map[string]bool{}
	for _, op := range ops {
		for _, t := range op.Targets {
			if t.EntityID == "" || t.EntityID == pricing.Remote || seen[t.EntityID] {
				continue
			}
			var c SupplyCandidate
			var priced bool
			switch op.Kind {
			case OpFurnitureOut, OpPack, OpPackInUse:
				c, priced = salvagePriced(resource, t, pricing)
			case OpCut:
				c, priced = cutPriced(resource, t, pricing.Crops, headroom)
			case OpHaulOut:
				distance := domain.Known(math.Hypot(float64(t.Minimum.X-home.X), float64(t.Minimum.Z-home.Z)))
				c, priced = haulPriced(resource, t, distance, headroom)
			}
			if priced {
				seen[t.EntityID] = true
				out = append(out, c)
			}
		}
	}
	return out
}

func salvagePriced(resource Resource, t ClearanceTarget, pricing ObstructionPricing) (SupplyCandidate, bool) {
	evidence, ok := pricing.Salvage[t.EntityID]
	if !ok {
		return SupplyCandidate{}, false
	}
	c := evidence.Candidate
	c.ID, c.Kind = t.EntityID, CandidateSalvage
	c.Yields = nil
	for _, y := range evidence.Candidate.Yields {
		if n, known := y.StockCap.Value(); y.Good.Def == resource && known && n > 0 {
			c.Yields = append(c.Yields, y)
		}
	}
	return c, len(c.Yields) > 0
}

func cutPriced(resource Resource, t ClearanceTarget, crops []CropChoice, headroom domain.Fact[int64]) (SupplyCandidate, bool) {
	for _, crop := range crops {
		if crop.Name != t.DefName {
			continue
		}
		harvests, known := crop.Harvests.Value()
		per, perKnown := crop.UnitsPerCell.Value()
		destroys, _ := crop.HarvestDestroys.Value()
		cells := int64(t.Maximum.X-t.Minimum.X+1) * int64(t.Maximum.Z-t.Minimum.Z+1)
		units := int64(math.Round(per)) * max(cells, 1)
		if !known || !perKnown || !destroys || harvests != resource || units <= 0 {
			return SupplyCandidate{}, false
		}
		return SourceCandidate(CandidateChop, t.EntityID, catalogLabor(CandidateChop, units), domain.Known(0.0), true, acquisitionUnitsPerTrip,
			SourceYield(ResourceKey{Def: resource}, units, 0, headroom)), true
	}
	return SupplyCandidate{}, false
}

func haulPriced(resource Resource, t ClearanceTarget, distance domain.Fact[float64], headroom domain.Fact[int64]) (SupplyCandidate, bool) {
	if Resource(t.DefName) != resource || t.Count <= 0 {
		return SupplyCandidate{}, false
	}
	return SourceCandidate(CandidateLoot, t.EntityID, catalogLabor(CandidateLoot, t.Count), distance, true, acquisitionUnitsPerTrip,
		SourceYield(ResourceKey{Def: resource}, t.Count, 0, headroom)), true
}
