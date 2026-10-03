package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

// electiveShares gates by a fixed remaining silver per pawn; a pawn absent
// from the map has an unknown share (necessities only).
func electiveShares(remaining map[PawnID]float64) ElectiveShare {
	return ElectiveShare{
		Items: ItemFacts{Market: map[Resource]float64{"BionicEye": 1000, "BionicArm": 1500}},
		Of: func(pawn PawnID) PersonalShare {
			r, ok := remaining[pawn]
			if !ok {
				return UnknownPersonalShare()
			}
			return PersonalShare{Share: domain.Known(r), Spent: domain.Known(0.0), Remaining: domain.Known(r), Gated: true}
		},
	}
}

// #1843: electives need the part's market value within the colonist's
// remaining share; served operations never pass the gate.
func TestElectiveSurgeryUnderShare(t *testing.T) {
	eye := func(id PawnID) CarePawn { return wholePawn(id, 0, electiveOp("InstallBionicEye", "Eye", 5, 0.97)) }
	queue := func(pawns []CarePawn, gate ElectiveShare) ([]SurgeryChoice, bool) {
		sel := SelectSurgery(domain.Known(pawns), nil, SurgeryContext{HospitalBed: true, Elective: gate})
		owed, _ := ElectiveSurgeryOwed(domain.Known(pawns), SurgeryContext{HospitalBed: true, Elective: gate}, nil).Value()
		return sel.Queue, owed
	}
	t.Run("poor gets none, rich gets one", func(t *testing.T) {
		q, owed := queue([]CarePawn{eye("a")}, electiveShares(map[PawnID]float64{"a": 100}))
		if len(q) != 0 || owed {
			t.Fatalf("poor: %+v owed %v", q, owed)
		}
		q, owed = queue([]CarePawn{eye("a")}, electiveShares(map[PawnID]float64{"a": 1000}))
		if len(q) != 1 || !owed {
			t.Fatalf("rich: %+v owed %v", q, owed)
		}
	})
	t.Run("hysteresis keeps a part just over the share", func(t *testing.T) {
		if q, _ := queue([]CarePawn{eye("a")}, electiveShares(map[PawnID]float64{"a": 920})); len(q) != 1 {
			t.Fatalf("within slack: %+v", q)
		}
		if q, _ := queue([]CarePawn{eye("a")}, electiveShares(map[PawnID]float64{"a": 900})); len(q) != 0 {
			t.Fatalf("beyond slack: %+v", q)
		}
	})
	t.Run("unknown share and unpriced part are necessities only", func(t *testing.T) {
		if q, owed := queue([]CarePawn{eye("a")}, electiveShares(nil)); len(q) != 0 || owed {
			t.Fatalf("unknown share: %+v owed %v", q, owed)
		}
		unpriced := wholePawn("a", 0, electiveOp("InstallBionicArm", "Arm", 20, 0.97))
		gate := electiveShares(map[PawnID]float64{"a": 1e9})
		gate.Items = ItemFacts{}
		if q, owed := queue([]CarePawn{unpriced}, gate); len(q) != 0 || owed {
			t.Fatalf("unpriced: %+v owed %v", q, owed)
		}
	})
	t.Run("one elective colony-wide, best affordable, ties by pawn id", func(t *testing.T) {
		q, _ := queue([]CarePawn{eye("b"), eye("a")}, electiveShares(map[PawnID]float64{"a": 1000, "b": 1000}))
		if len(q) != 1 || q[0].Pawn != "a" {
			t.Fatalf("tie: %+v", q)
		}
		// The arm gains more but is unaffordable, so the eye is chosen.
		mixed := []CarePawn{wholePawn("a", 0, electiveOp("InstallBionicArm", "Arm", 20, 0.97)), eye("b")}
		q, _ = queue(mixed, electiveShares(map[PawnID]float64{"a": 500, "b": 1000}))
		if len(q) != 1 || q[0].Pawn != "b" {
			t.Fatalf("affordable only: %+v", q)
		}
	})
	t.Run("served operations stay free and first", func(t *testing.T) {
		peg := wholePawn("b", 0, restoreOp("InstallPegLeg", "Leg", 30, 0.9, 1, true))
		q, _ := queue([]CarePawn{eye("a"), peg}, electiveShares(nil))
		if len(q) != 1 || q[0].Recipe != "InstallPegLeg" || q[0].Elective {
			t.Fatalf("served pawn with unknown share: %+v", q)
		}
	})
	t.Run("zero value is ungated", func(t *testing.T) {
		if q, owed := queue([]CarePawn{eye("a")}, ElectiveShare{}); len(q) != 1 || !owed {
			t.Fatalf("ungated: %+v owed %v", q, owed)
		}
	})
}
