package bridge

import (
	"math"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
)

// validateOdysseyColony checks the Odyssey colony section (#1709): ids are
// unique per table, cells lie on the map, counts and ticks are nonnegative
// and a hackable's progress is a fraction. Absent scalars stay unknown.
func validateOdysseyColony(v *o.ColonyFactsSnapshot) error {
	if v.Odyssey == nil {
		return nil
	}
	switch s := v.Odyssey.Outcome.(type) {
	case *o.OdysseySection_Unavailable:
		return validateUnavailable(s.Unavailable)
	case *o.OdysseySection_Observed:
		f := s.Observed
		if f == nil {
			return contract("incomplete odyssey colony facts")
		}
		ids := map[string]bool{}
		for _, r := range f.Conditions {
			if r == nil || validID(r.GetConditionId()) != nil || ids["c"+r.GetConditionId()] || validID(r.GetDefName()) != nil || validID(r.GetConditionClass()) != nil ||
				r.TicksPassed != nil && r.GetTicksPassed() < 0 || r.TicksLeft != nil && r.GetTicksLeft() < 0 || r.Permanent == nil ||
				r.GetPermanent() && r.TicksLeft != nil || r.CauserId != nil && validID(r.GetCauserId()) != nil {
				return contract("invalid odyssey game condition")
			}
			ids["c"+r.GetConditionId()] = true
		}
		for _, r := range f.HazardTerrain {
			if r == nil || validID(r.GetDefName()) != nil || ids["t"+r.GetDefName()] || r.Cells == nil || r.GetCells() == 0 || r.BurnDamage != nil && r.GetBurnDamage() < 0 ||
				badNonNegative(r.HeatPerTick) || badNonNegative(r.ToxicBuildupFactor) {
				return contract("invalid odyssey hazard terrain")
			}
			ids["t"+r.GetDefName()] = true
		}
		for _, r := range f.LavaEmergences {
			if r == nil || validID(r.GetThingId()) != nil || ids["l"+r.GetThingId()] || !colonyCell(r.Position, v.MapSize) {
				return contract("invalid odyssey lava emergence")
			}
			ids["l"+r.GetThingId()] = true
		}
		maps := map[int32]bool{}
		for _, r := range f.Sites {
			if r == nil || validID(r.GetHatchId()) != nil || ids["h"+r.GetHatchId()] || r.PocketMapId == nil || maps[r.GetPocketMapId()] ||
				r.StockpileType != nil && validID(r.GetStockpileType()) != nil || r.Layout != nil && validID(r.GetLayout()) != nil {
				return contract("invalid odyssey underground site")
			}
			ids["h"+r.GetHatchId()] = true
			maps[r.GetPocketMapId()] = true
			hacks := map[string]bool{}
			for _, h := range r.Hackables {
				if h == nil || validID(h.GetThingId()) != nil || hacks[h.GetThingId()] || validID(h.GetDefName()) != nil || h.Position == nil ||
					h.ProgressPercent != nil && (math.IsNaN(h.GetProgressPercent()) || h.GetProgressPercent() < 0 || h.GetProgressPercent() > 1) || badNonNegative(h.Defence) {
					return contract("invalid odyssey hackable")
				}
				hacks[h.GetThingId()] = true
			}
		}
		return nil
	default:
		return contract("missing odyssey colony outcome")
	}
}

func badNonNegative(v *float64) bool {
	return v != nil && (math.IsNaN(*v) || math.IsInf(*v, 0) || *v < 0)
}
