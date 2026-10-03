package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"math"
)

func validateApparelPolicy(v *o.ApparelPolicyState) error {
	if v == nil {
		return nil
	}
	if buildingUnknown(v) != nil || validID(v.GetToken()) != nil || v.Child == nil || v.Slave == nil || v.IncapableOfViolence == nil || v.Drafted == nil {
		return contract("invalid apparel policy census")
	}
	if validID(v.GetName()) != nil || v.MinHitPoints == nil || v.MaxHitPoints == nil || math.IsNaN(float64(v.GetMinHitPoints())) || math.IsNaN(float64(v.GetMaxHitPoints())) || v.GetMinHitPoints() < 0 || v.GetMaxHitPoints() > 1 || v.GetMinHitPoints() > v.GetMaxHitPoints() || v.MinQuality == nil || v.MaxQuality == nil || v.GetMinQuality() < 0 || v.GetMaxQuality() > 6 || v.GetMinQuality() > v.GetMaxQuality() || v.ExcludesTainted == nil {
		return contract("incomplete apparel policy")
	}
	// The outfit filter may allow apparel this pawn cannot wear (a child's
	// or another body's): only ids and duplicates are checked.
	allowed := map[string]bool{}
	for _, d := range v.AllowedDefs {
		if validID(d) != nil || allowed[d] {
			return contract("invalid apparel filter")
		}
		allowed[d] = true
	}
	seen := map[string]bool{}
	for _, w := range v.Work {
		if w == nil || validID(w.GetDefName()) != nil || seen[w.GetDefName()] || w.Priority == nil || w.GetPriority() < 0 || w.GetPriority() > 4 || w.Disabled == nil {
			return contract("invalid apparel work role")
		}
		seen[w.GetDefName()] = true
	}
	seen = map[string]bool{}
	for _, s := range v.Skills {
		if s == nil || validID(s.GetDefinition().GetDefName()) != nil || seen[s.GetDefinition().GetDefName()] || s.Level == nil || s.GetLevel() < 0 || s.GetLevel() > 20 || s.Passion == nil || s.Disabled == nil {
			return contract("invalid apparel skill census")
		}
		seen[s.GetDefinition().GetDefName()] = true
	}
	return nil
}
