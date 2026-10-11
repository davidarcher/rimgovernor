package bridge

import (
	"math"

	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
)

// Bounds on the scenario parts a NewColonySpec may carry.
const (
	NewColonyMaxPartsPerKind = 64
	NewColonyMaxThingCount   = 100_000
	NewColonyMaxTraitDegree  = 10
	NewColonyMaxSeverity     = 1
)

// validateNewColonyParts checks the scenario parts' presence and ranges.
// Whether a defName exists stays native's call.
func validateNewColonyParts(spec *l.NewColonySpec) error {
	for _, n := range []int{
		len(spec.StartingThings), len(spec.ForcedTraits), len(spec.ForcedHediffs), len(spec.Naked), len(spec.SetNeedLevels),
		len(spec.PermaGameConditions), len(spec.DisallowedBuildings), len(spec.DisabledIncidents),
	} {
		if n > NewColonyMaxPartsPerKind {
			return contract("new colony scenario parts exceed %d per kind", NewColonyMaxPartsPerKind)
		}
	}
	for _, t := range spec.StartingThings {
		if t == nil || !validPlacement(t.Placement) || t.ThingDef == nil || validID(t.GetThingDef()) != nil ||
			!optionalID(t.Stuff) || !optionalID(t.Quality) {
			return contract("new colony starting thing invalid")
		}
		if t.Count == nil || t.GetCount() < 1 || t.GetCount() > NewColonyMaxThingCount {
			return contract("new colony starting thing count out of range")
		}
		if t.AllowRoofed != nil && t.GetPlacement() != l.StartingThingPlacement_STARTING_THING_PLACEMENT_SCATTERED {
			return contract("new colony starting thing allow roofed applies to scattered things only")
		}
	}
	for _, t := range spec.ForcedTraits {
		if t == nil || t.Trait == nil || validID(t.GetTrait()) != nil || t.Degree == nil ||
			t.GetDegree() < -NewColonyMaxTraitDegree || t.GetDegree() > NewColonyMaxTraitDegree {
			return contract("new colony forced trait invalid or degree out of range")
		}
		if err := validPartChanceContext(t.Chance, t.Context, "forced trait"); err != nil {
			return err
		}
	}
	for _, h := range spec.ForcedHediffs {
		if h == nil || h.Hediff == nil || validID(h.GetHediff()) != nil || !inRange(h.Severity, 0, NewColonyMaxSeverity) {
			return contract("new colony forced hediff invalid or severity out of range")
		}
		if err := validPartChanceContext(h.Chance, h.Context, "forced hediff"); err != nil {
			return err
		}
	}
	for _, n := range spec.Naked {
		if n == nil {
			return contract("new colony naked part invalid")
		}
		if err := validPartChanceContext(n.Chance, n.Context, "naked"); err != nil {
			return err
		}
	}
	for _, n := range spec.SetNeedLevels {
		if n == nil || n.Need == nil || validID(n.GetNeed()) != nil || !inRange(n.Level, 0, 1) {
			return contract("new colony need level invalid or out of range")
		}
		if err := validPartChanceContext(n.Chance, n.Context, "need level"); err != nil {
			return err
		}
	}
	for name, ids := range map[string][]string{
		"perma game conditions": spec.PermaGameConditions,
		"disallowed buildings":  spec.DisallowedBuildings, "disabled incidents": spec.DisabledIncidents,
	} {
		if !validIDs(ids, true) {
			return contract("new colony %s invalid", name)
		}
	}
	if f := spec.PlayerFaction; f != nil {
		if _, ok := l.ScenarioPlayerFaction_name[int32(*f)]; !ok || *f == l.ScenarioPlayerFaction_SCENARIO_PLAYER_FACTION_UNSPECIFIED {
			return contract("new colony player faction invalid")
		}
	}
	if a := spec.ArrivalMethod; a != nil {
		if _, ok := l.ScenarioArrivalMethod_name[int32(*a)]; !ok || *a == l.ScenarioArrivalMethod_SCENARIO_ARRIVAL_METHOD_UNSPECIFIED {
			return contract("new colony arrival method invalid")
		}
	}
	return nil
}

func inRange(v *float32, lo, hi float64) bool {
	if v == nil {
		return false
	}
	f := float64(*v)
	return !math.IsNaN(f) && f >= lo && f <= hi
}

func validPartChanceContext(chance *float32, context *l.ScenarioPawnContext, what string) error {
	if !inRange(chance, 0, 1) {
		return contract("new colony %s chance out of range", what)
	}
	if context == nil || *context == l.ScenarioPawnContext_SCENARIO_PAWN_CONTEXT_UNSPECIFIED {
		return contract("new colony %s requires a context", what)
	}
	if _, ok := l.ScenarioPawnContext_name[int32(*context)]; !ok {
		return contract("new colony %s context invalid", what)
	}
	return nil
}

func validPlacement(p *l.StartingThingPlacement) bool {
	if p == nil || *p == l.StartingThingPlacement_STARTING_THING_PLACEMENT_UNSPECIFIED {
		return false
	}
	_, ok := l.StartingThingPlacement_name[int32(*p)]
	return ok
}
