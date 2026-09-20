package bridge

import (
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"math"
)

func validateApparelPolicy(v *o.ApparelPolicyState) error {
	if v == nil {
		return nil
	}
	if buildingUnknown(v) != nil || validID(v.GetToken()) != nil || v.Child == nil || v.Slave == nil || v.IncapableOfViolence == nil || v.Drafted == nil || len(v.Definitions) > 512 || len(v.AllowedDefs) > 512 || len(v.Work) > 256 {
		return contract("invalid apparel policy census")
	}
	if validID(v.GetName()) != nil || v.MinHitPoints == nil || v.MaxHitPoints == nil || math.IsNaN(float64(v.GetMinHitPoints())) || math.IsNaN(float64(v.GetMaxHitPoints())) || v.GetMinHitPoints() < 0 || v.GetMaxHitPoints() > 1 || v.GetMinHitPoints() > v.GetMaxHitPoints() || v.MinQuality == nil || v.MaxQuality == nil || v.GetMinQuality() < 0 || v.GetMaxQuality() > 6 || v.GetMinQuality() > v.GetMaxQuality() || v.ExcludesTainted == nil {
		return contract("incomplete apparel policy")
	}
	seen := map[string]bool{}
	for _, d := range v.Definitions {
		if d == nil || validID(d.GetDefName()) != nil || seen[d.GetDefName()] || d.Armor == nil || d.Child == nil || d.Adult == nil {
			return contract("invalid apparel definition census")
		}
		seen[d.GetDefName()] = true
	}
	allowed := map[string]bool{}
	for _, d := range v.AllowedDefs {
		if !seen[d] || allowed[d] {
			return contract("invalid apparel filter")
		}
		allowed[d] = true
	}
	seen = map[string]bool{}
	for _, w := range v.Work {
		if w == nil || validID(w.GetDefName()) != nil || seen[w.GetDefName()] || w.Priority == nil || w.GetPriority() < 0 || w.GetPriority() > 4 || w.Disabled == nil {
			return contract("invalid apparel work role")
		}
		seen[w.GetDefName()] = true
	}
	return nil
}
