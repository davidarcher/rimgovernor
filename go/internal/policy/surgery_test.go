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
			got := SelectSurgery(domain.Known(c.pawns), c.inFlight, SurgeryContext{})
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
			got := SelectSurgery(domain.Known([]CarePawn{c.pawn}), nil, SurgeryContext{})
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

// wholePawn reads no chronic condition, so an install is not a cure.
func wholePawn(id PawnID, queued int, ops ...SurgeryOperation) CarePawn {
	p := surgeryPawn(id, queued, ops...)
	p.Conditions = domain.Known([]CareCondition(nil))
	return p
}

func electiveOp(recipe, part string, index int, chance float64) SurgeryOperation {
	op := restoreOp(recipe, part, index, chance, 1, true)
	op.Kind = SurgeryInstall
	return op
}

func TestElectiveSurgery(t *testing.T) {
	shooter := PawnProfile{ID: "a", Ranged: true, Skills: map[string]ProfileSkill{"Shooting": {Name: "Shooting", Level: 12}, "Construction": {Name: "Construction", Level: 2}}}
	builder := PawnProfile{ID: "a", Skills: map[string]ProfileSkill{"Shooting": {Name: "Shooting", Level: 12}, "Construction": {Name: "Construction", Level: 14}}}
	upgrades := func() CarePawn {
		return wholePawn("a", 0, electiveOp("InstallBionicEye", "Eye", 5, 0.97), electiveOp("InstallBionicArm", "Arm", 20, 0.97))
	}
	ward := SurgeryContext{HospitalBed: true}
	for _, c := range []struct {
		name    string
		pawns   []CarePawn
		ctx     SurgeryContext
		recipes []string
		wants   int
	}{
		{"shooter gets the eye", []CarePawn{upgrades()}, SurgeryContext{HospitalBed: true, Profiles: []PawnProfile{shooter}}, []string{"InstallBionicEye"}, 0},
		{"worker gets the arm", []CarePawn{upgrades()}, SurgeryContext{HospitalBed: true, Profiles: []PawnProfile{builder}}, []string{"InstallBionicArm"}, 0},
		{"one elective, to the shooter", []CarePawn{wholePawn("0", 0, electiveOp("InstallBionicEye", "Eye", 5, 0.97)), upgrades()},
			SurgeryContext{HospitalBed: true, Profiles: []PawnProfile{shooter}}, []string{"InstallBionicEye"}, 0},
		{"no hospital bed", []CarePawn{upgrades()}, SurgeryContext{}, nil, 0},
		{"over the 5% cap", []CarePawn{wholePawn("a", 0, electiveOp("InstallBionicEye", "Eye", 5, 0.94))}, ward, nil, 0},
		{"5% cap holds", []CarePawn{wholePawn("a", 0, electiveOp("InstallBionicEye", "Eye", 5, 0.95))}, ward, []string{"InstallBionicEye"}, 0},
		{"not an upgrade", []CarePawn{wholePawn("a", 0, electiveOp("InstallSimpleProstheticArm", "Arm", 20, 0.99), electiveOp("InstallJoywire", "Brain", 1, 0.99))}, ward, nil, 0},
		{"restore pending elsewhere", []CarePawn{upgrades(), wholePawn("b", 0, restoreOp("InstallPegLeg", "Leg", 30, 0.5, 1, true))}, ward, nil, 1},
		{"restore blocks electives", []CarePawn{upgrades(), wholePawn("b", 0, restoreOp("InstallPegLeg", "Leg", 30, 0.9, 1, true))}, ward, []string{"InstallPegLeg"}, 0},
	} {
		t.Run(c.name, func(t *testing.T) {
			got := SelectSurgery(domain.Known(c.pawns), nil, c.ctx)
			if len(got.Queue) != len(c.recipes) || len(got.Wants) != c.wants {
				t.Fatalf("queue %+v wants %+v", got.Queue, got.Wants)
			}
			for i, r := range c.recipes {
				if got.Queue[i].Recipe != r {
					t.Fatalf("queue %+v", got.Queue)
				}
			}
			owed, _ := ElectiveSurgeryOwed(domain.Known(c.pawns), domain.Known(c.ctx.HospitalBed)).Value()
			if elective := len(got.Queue) > 0 && got.Queue[0].Kind == SurgeryInstall; owed != elective {
				t.Fatalf("owed %v, queue %+v", owed, got.Queue)
			}
		})
	}
}

func TestHospitalBedReady(t *testing.T) {
	bed := func(medical, prisoners bool) SleepingBed {
		return SleepingBed{Humanlike: domain.Known(true), Medical: domain.Known(medical), Prisoners: domain.Known(prisoners)}
	}
	for _, c := range []struct {
		beds []SleepingBed
		want bool
	}{{nil, false}, {[]SleepingBed{bed(false, false)}, false}, {[]SleepingBed{bed(true, true)}, false}, {[]SleepingBed{bed(false, false), bed(true, false)}, true}} {
		if v, k := HospitalBedReady(domain.Known(SleepingObservation{Beds: c.beds})).Value(); !k || v != c.want {
			t.Fatalf("%+v: %v", c.beds, v)
		}
	}
}
