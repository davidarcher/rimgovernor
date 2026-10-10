package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// HazardKind names an outdoor hazard that holds outdoor work. Scope is the
// three below; fire, flashstorm and the rest are follow-ups. Any other kind
// in a known hazard list holds too: unknown kinds fail closed.
type HazardKind string

const (
	HazardExtremeCold  HazardKind = "extreme_cold"
	HazardExtremeHeat  HazardKind = "extreme_heat"
	HazardToxicFallout HazardKind = "toxic_fallout"
)

// OutdoorHazard is one active hazard on the map.
type OutdoorHazard struct {
	Kind HazardKind
}

// RemoteHoldHazard: an outdoor hazard (extreme cold or heat, toxic fallout)
// is active; outdoor work waits and indoor work is untouched.
const RemoteHoldHazard = "outdoor_hazard"

// OutdoorHazardsOf derives the active hazards: toxic fallout from the
// condition census, cold and heat from the outdoor temperature at or past
// the extreme bounds. Unknown while either read is missing, so a map
// without the reads is never held for it.
func OutdoorHazardsOf(f RoundsFacts) domain.Fact[[]OutdoorHazard] {
	conditions, ck := f.DisasterConditions.Value()
	temp, tk := f.OutdoorTemperature.Value()
	if !ck || !tk {
		return domain.Unknown[[]OutdoorHazard]()
	}
	var out []OutdoorHazard
	for _, c := range conditions {
		if c.Definition == ConditionToxicFallout {
			out = append(out, OutdoorHazard{HazardToxicFallout})
			break
		}
	}
	if temp <= extremeColdC {
		out = append(out, OutdoorHazard{HazardExtremeCold})
	}
	if temp >= extremeHeatC {
		out = append(out, OutdoorHazard{HazardExtremeHeat})
	}
	return domain.Known(out)
}

// OutdoorHazardHeld reports whether outdoor work is held: any known active
// hazard, including a kind this build does not recognise. Unknown never holds.
func OutdoorHazardHeld(h domain.Fact[[]OutdoorHazard]) bool {
	hazards, known := h.Value()
	return known && len(hazards) > 0
}

// OutdoorAcquisitionHeld is the chop, hunt and forage gate: the same hold
// the remote requests apply, read from the review facts.
func OutdoorAcquisitionHeld(f RoundsFacts) bool { return OutdoorHazardHeld(OutdoorHazardsOf(f)) }
