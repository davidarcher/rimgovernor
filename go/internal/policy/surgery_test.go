package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func restoreOp(recipe, part string, index int, chance float64, doctors int, stocked bool) SurgeryOperation {
	return SurgeryOperation{Recipe: domain.Known(recipe), PartDefName: domain.Known(part), PartIndex: domain.Known(index), Kind: SurgeryRestore,
		SuccessChance: domain.Known(chance), EligibleDoctors: domain.Known(doctors), IngredientsOnMap: domain.Known(stocked),
		Violation: domain.Known(false), Lethal: domain.Known(false)}
}

func surgeryPawn(id PawnID, queued int, ops ...SurgeryOperation) CarePawn {
	return CarePawn{ID: id, Dead: domain.Known(false), QueuedSurgeries: domain.Known(queued), Operations: domain.Known(ops)}
}

func TestSelectSurgery(t *testing.T) {
	leg := func(chance float64, doctors int, peg, prosthetic, bionic bool) []SurgeryOperation {
		return []SurgeryOperation{
			restoreOp("InstallPegLeg", "Leg", 30, chance, doctors, peg),
			restoreOp("InstallSimpleProstheticLeg", "Leg", 30, chance, doctors, prosthetic),
			restoreOp("InstallBionicLeg", "Leg", 30, chance, doctors, bionic),
		}
	}
	for _, c := range []struct {
		name     string
		pawns    []CarePawn
		inFlight map[PawnID]bool
		recipes  []string
		wants    []SurgeryWantReason
	}{
		{"best stocked part", []CarePawn{surgeryPawn("a", 0, leg(0.9, 1, true, true, false)...)}, nil, []string{"InstallSimpleProstheticLeg"}, nil},
		{"bionic over prosthetic", []CarePawn{surgeryPawn("a", 0, leg(0.9, 1, true, true, true)...)}, nil, []string{"InstallBionicLeg"}, nil},
		{"20% cap holds", []CarePawn{surgeryPawn("a", 0, leg(0.8, 1, true, false, false)...)}, nil, []string{"InstallPegLeg"}, nil},
		{"over the cap", []CarePawn{surgeryPawn("a", 0, leg(0.79, 1, true, false, false)...)}, nil, nil, []SurgeryWantReason{SurgeryNoDoctor}},
		{"no eligible doctor", []CarePawn{surgeryPawn("a", 0, leg(0.95, 0, true, false, false)...)}, nil, nil, []SurgeryWantReason{SurgeryNoDoctor}},
		{"part short", []CarePawn{surgeryPawn("a", 0, leg(0.95, 1, false, false, false)...)}, nil, nil, []SurgeryWantReason{SurgeryPartShort}},
		{"bill queued", []CarePawn{surgeryPawn("a", 1, leg(0.9, 1, true, false, false)...)}, nil, nil, nil},
		{"open action", []CarePawn{surgeryPawn("a", 0, leg(0.9, 1, true, false, false)...)}, map[PawnID]bool{"a": true}, nil, nil},
		{"one per patient, leg before eye", []CarePawn{surgeryPawn("a", 0,
			restoreOp("InstallBionicEye", "Eye", 5, 0.9, 1, true), restoreOp("InstallSimpleProstheticLeg", "Leg", 30, 0.9, 1, true))}, nil, []string{"InstallSimpleProstheticLeg"}, nil},
		{"ranked across patients", []CarePawn{
			surgeryPawn("a", 0, restoreOp("InstallPegLeg", "Leg", 30, 0.9, 1, true)),
			surgeryPawn("b", 0, restoreOp("InstallBionicLeg", "Leg", 30, 0.9, 1, true))}, nil, []string{"InstallBionicLeg", "InstallPegLeg"}, nil},
		{"not a restore", []CarePawn{surgeryPawn("a", 0, SurgeryOperation{Recipe: domain.Known("Anesthetize"), PartIndex: domain.Known(-1), Kind: SurgeryOther})}, nil, nil, nil},
	} {
		t.Run(c.name, func(t *testing.T) {
			got := SelectSurgery(domain.Known(c.pawns), c.inFlight)
			if len(got.Queue) != len(c.recipes) {
				t.Fatalf("queue %+v", got.Queue)
			}
			for i, r := range c.recipes {
				if got.Queue[i].Recipe != r {
					t.Fatalf("queue %+v", got.Queue)
				}
			}
			if len(got.Wants) != len(c.wants) {
				t.Fatalf("wants %+v", got.Wants)
			}
			for i, w := range c.wants {
				if got.Wants[i].Reason != w {
					t.Fatalf("wants %+v", got.Wants)
				}
			}
		})
	}
}

func TestSurgeryRecovered(t *testing.T) {
	if _, known := SurgeryRecovered(domain.Unknown[[]CarePawn]()).Value(); known {
		t.Fatal("unknown census recovered")
	}
	if v, _ := SurgeryRecovered(domain.Known([]CarePawn{surgeryPawn("a", 1, restoreOp("InstallPegLeg", "Leg", 30, 0.9, 1, true))})).Value(); v {
		t.Fatal("a queued bill settled the goal")
	}
	dead := surgeryPawn("a", 0, restoreOp("InstallPegLeg", "Leg", 30, 0.9, 1, true))
	dead.Dead = domain.Known(true)
	if v, k := SurgeryRecovered(domain.Known([]CarePawn{dead, surgeryPawn("b", 0)})).Value(); !k || !v {
		t.Fatal("healthy colony not recovered")
	}
}
