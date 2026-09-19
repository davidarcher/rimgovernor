package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"sort"
)

const ClearHomeObstructions GoalID = "ClearHomeObstructions"

// ClearanceTarget is a complete native building observation. Admission remains
// subject to a fresh observation and the native counterfactual roof check.
type ClearanceTarget struct {
	EntityID, DefName                      string
	Minimum, Maximum                       domain.Cell
	Faction                                string
	Class                                  string
	Deconstructible, InHome, AncientDanger bool
	RoofBlocker                            string
	Designated, ControllerOwned            bool
}

type ClearanceHold struct{ Target, Reason string }
type ClearanceSelection struct {
	Targets []ClearanceTarget
	Holds   []ClearanceHold
}

func ClearanceHoldReason(row ClearanceTarget) string {
	switch {
	case !row.InHome:
		return "outside_home"
	case !row.Deconstructible:
		return "not_deconstructible"
	case row.Faction == "Player":
		return "player_building"
	case row.RoofBlocker != "":
		return "roof_blocker"
	case row.AncientDanger:
		return "ancient_danger"
	case row.Class == "ancient_casket":
		return "casket"
	case row.Designated && !row.ControllerOwned:
		return "foreign_designation"
	case row.Designated:
		return "owned_designation"
	}
	return ""
}

// SelectHomeClearance admits one target so removals cannot jointly invalidate
// the individually observed roof support. Distance ties use stable identities.
func SelectHomeClearance(rows []ClearanceTarget, center domain.Cell) ClearanceSelection {
	out := ClearanceSelection{}
	for _, row := range rows {
		if reason := ClearanceHoldReason(row); reason != "" {
			out.Holds = append(out.Holds, ClearanceHold{row.EntityID, reason})
		} else {
			out.Targets = append(out.Targets, row)
		}
	}
	distance := func(r ClearanceTarget) float64 {
		x := (float64(r.Minimum.X)+float64(r.Maximum.X))/2 - float64(center.X)
		z := (float64(r.Minimum.Z)+float64(r.Maximum.Z))/2 - float64(center.Z)
		return x*x + z*z
	}
	sort.Slice(out.Targets, func(i, j int) bool {
		a, b := out.Targets[i], out.Targets[j]
		if distance(a) != distance(b) {
			return distance(a) < distance(b)
		}
		return a.EntityID < b.EntityID
	})
	sort.Slice(out.Holds, func(i, j int) bool { return out.Holds[i].Target < out.Holds[j].Target })
	if len(out.Targets) > 1 {
		out.Targets = out.Targets[:1]
	}
	return out
}
