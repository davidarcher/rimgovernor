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

func chronicPawn(id PawnID, conditions []CareCondition, ops ...SurgeryOperation) CarePawn {
	p := surgeryPawn(id, 0, ops...)
	p.Conditions = domain.Known(conditions)
	return p
}

func chronic(def string, part int) CareCondition {
	c := CareCondition{DefName: domain.Known(def)}
	if part >= 0 {
		c.PartIndex = domain.Known(part)
	}
	return c
}

func kindOp(kind SurgeryKind, recipe, part string, index int, chance float64, stocked bool) SurgeryOperation {
	op := restoreOp(recipe, part, index, chance, 1, stocked)
	op.Kind = kind
	if index < 0 {
		op.PartIndex, op.PartDefName = domain.Unknown[int](), domain.Unknown[string]()
	}
	return op
}

func TestSelectSurgeryChronic(t *testing.T) {
	cataract := []CareCondition{chronic("Cataract", 5)}
	for _, c := range []struct {
		name   string
		pawn   CarePawn
		recipe string
		value  float64
		wants  []SurgeryWantReason
	}{
		{"cataract gets a bionic eye", chronicPawn("a", cataract, kindOp(SurgeryInstall, "InstallBionicEye", "Eye", 5, 0.9, true)), "InstallBionicEye", 1.25 * 0.6, nil},
		{"healthy eye gets nothing", chronicPawn("a", nil, kindOp(SurgeryInstall, "InstallBionicEye", "Eye", 5, 0.9, true)), "", 0, nil},
		{"other eye gets nothing", chronicPawn("a", cataract, kindOp(SurgeryInstall, "InstallBionicEye", "Eye", 6, 0.9, true)), "", 0, nil},
		{"implant is no cure", chronicPawn("a", []CareCondition{chronic("Dementia", 2)}, kindOp(SurgeryInstall, "InstallJoywire", "Brain", 2, 0.9, true)), "", 0, nil},
		{"part short", chronicPawn("a", cataract, kindOp(SurgeryInstall, "InstallBionicEye", "Eye", 5, 0.9, false)), "", 0, []SurgeryWantReason{SurgeryPartShort}},
		{"cure over the 20% cap", chronicPawn("a", cataract, kindOp(SurgeryCure, "CureCataract", "Eye", 5, 0.7, true)), "", 0, []SurgeryWantReason{SurgeryNoDoctor}},
		{"whole-body cure", chronicPawn("a", []CareCondition{chronic("Frail", -1)}, kindOp(SurgeryCure, "CureFrail", "", -1, 0.85, true)), "CureFrail", 0.8, nil},
		{"non-chronic cure ignored", chronicPawn("a", []CareCondition{chronic("Flu", -1)}, kindOp(SurgeryCure, "CureFlu", "", -1, 0.9, true)), "", 0, nil},
		{"bad back beats cataract", chronicPawn("a", []CareCondition{chronic("Cataract", 5), chronic("BadBack", 3)},
			kindOp(SurgeryInstall, "InstallBionicEye", "Eye", 5, 0.9, true), kindOp(SurgeryInstall, "InstallBionicSpine", "Spine", 3, 0.9, true)), "InstallBionicSpine", 1.25 * 0.8, nil},
	} {
		t.Run(c.name, func(t *testing.T) {
			got := SelectSurgery(domain.Known([]CarePawn{c.pawn}), nil)
			if c.recipe == "" && len(got.Queue) != 0 || c.recipe != "" && (len(got.Queue) != 1 || got.Queue[0].Recipe != c.recipe || got.Queue[0].Value != c.value) {
				t.Fatalf("queue %+v", got.Queue)
			}
			if len(got.Wants) != len(c.wants) || len(c.wants) == 1 && got.Wants[0].Reason != c.wants[0] {
				t.Fatalf("wants %+v", got.Wants)
			}
			if v, k := SurgeryRecovered(domain.Known([]CarePawn{c.pawn})).Value(); !k || v != (c.recipe == "" && c.wants == nil) {
				t.Fatalf("recovered %v %v", v, k)
			}
		})
	}
	unread := surgeryPawn("a", 0, kindOp(SurgeryCure, "CureCataract", "Eye", 5, 0.9, true))
	if _, k := SurgeryRecovered(domain.Known([]CarePawn{unread})).Value(); k {
		t.Fatal("unread conditions recovered")
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
