package policy

import (
	"math"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func infection(part int, severity, severityPerDay, immunity, immunityPerDay float64) CareCondition {
	return CareCondition{DefName: domain.Known("WoundInfection"), PartIndex: domain.Known(part),
		Severity: domain.Known(severity), SeverityPerDay: domain.Known(severityPerDay),
		Immunity: domain.Known(immunity), ImmunityPerDay: domain.Known(immunityPerDay)}
}

func amputate(part int, success float64) SurgeryOperation {
	return SurgeryOperation{Recipe: domain.Known("RemoveBodyPart"), PartIndex: domain.Known(part), Kind: SurgeryAmputate,
		SuccessChance: domain.Known(success), EligibleDoctors: domain.Known(1), IngredientsOnMap: domain.Known(true),
		Violation: domain.Known(false), Lethal: domain.Known(false)}
}

func TestLosingImmunityRace(t *testing.T) {
	for _, tc := range []struct {
		name          string
		c             CareCondition
		losing, known bool
	}{
		{"severity first", infection(7, 0.5, 0.25, 0.2, 0.1), true, true},  // 2 days vs 8 days
		{"immunity first", infection(7, 0.2, 0.1, 0.5, 0.25), false, true}, // 8 days vs 2 days
		{"tie loses", infection(7, 0.5, 0.25, 0.5, 0.25), true, true},
		{"no immunity gain", infection(7, 0.1, 0.01, 0.1, 0), true, true},
		{"receding", infection(7, 0.9, -0.1, 0, 0), false, true},
		{"immune", infection(7, 0.9, 0.5, 1, 0), false, true},
		{"nan", infection(7, 0.5, math.NaN(), 0.2, 0.1), false, false},
		{"unknown", CareCondition{Severity: domain.Known(0.5)}, false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if losing, known := LosingImmunityRace(tc.c); losing != tc.losing || known != tc.known {
				t.Fatalf("got %v %v", losing, known)
			}
		})
	}
}

func TestLifeSavingAmputations(t *testing.T) {
	losing := infection(7, 0.6, 0.3, 0.1, 0.1)
	pawn := func(conditions []CareCondition, ops ...SurgeryOperation) CarePawn {
		return CarePawn{ID: "p", Dead: domain.Known(false), Conditions: domain.Known(conditions), Operations: domain.Known(ops)}
	}
	for _, tc := range []struct {
		name string
		pawn CarePawn
		want []int
	}{
		{"losing hand is amputated", pawn([]CareCondition{losing}, amputate(3, 0.9), amputate(7, 0.9)), []int{7}},
		{"winning infection is left", pawn([]CareCondition{infection(7, 0.2, 0.1, 0.6, 0.3)}, amputate(7, 0.9)), nil},
		{"no amputation on the part (torso)", pawn([]CareCondition{losing}, amputate(3, 0.9)), nil},
		{"zero success chance", pawn([]CareCondition{losing}, amputate(7, 0)), nil},
		{"low success still beats death", pawn([]CareCondition{losing}, amputate(7, 0.05)), []int{7}},
		{"unknown success (no doctor)", pawn([]CareCondition{losing}, SurgeryOperation{Recipe: domain.Known("RemoveBodyPart"), PartIndex: domain.Known(7), Kind: SurgeryAmputate}), nil},
		{"other disease", pawn([]CareCondition{{DefName: domain.Known("Flu"), PartIndex: domain.Known(7), Severity: domain.Known(0.6), SeverityPerDay: domain.Known(0.3), Immunity: domain.Known(0.1), ImmunityPerDay: domain.Known(0.1)}}, amputate(7, 0.9)), nil},
		{"two limbs ordered", pawn([]CareCondition{infection(9, 0.6, 0.3, 0.1, 0.1), losing}, amputate(9, 0.9), amputate(7, 0.9)), []int{7, 9}},
		{"dead", CarePawn{ID: "p", Dead: domain.Known(true), Conditions: domain.Known([]CareCondition{losing}), Operations: domain.Known([]SurgeryOperation{amputate(7, 0.9)})}, nil},
	} {
		t.Run(tc.name, func(t *testing.T) {
			got := LifeSavingAmputations(tc.pawn)
			if len(got) != len(tc.want) {
				t.Fatalf("got %v want parts %v", got, tc.want)
			}
			for i, s := range got {
				if s.Part() != tc.want[i] || s.Recipe() != "RemoveBodyPart" || s.Pawn() != "p" || s.AcknowledgeViolation() {
					t.Fatalf("row %d: %+v", i, s)
				}
			}
		})
	}
}

func TestAmputationNeedsCountsOnlyNewPatients(t *testing.T) {
	current := domain.GenerationSnapshot{Colony: "colony", Load: "load", Plan: "plan"}
	up := func(id PawnID, bleeding bool) EmergencyPawn {
		return EmergencyPawn{ID: id, Dead: domain.Known(false), Downed: domain.Known(false), Bleeding: domain.Known(bleeding), NeedsTend: domain.Known(bleeding), InBed: domain.Known(true)}
	}
	snapshot, err := NewEmergencySnapshot(current, 7, EmergencyFacts{ColonistsComplete: domain.Known(true), Colonists: []EmergencyPawn{up("infected", false), up("bleeding", true)}})
	if err != nil {
		t.Fatal(err)
	}
	infected := func(id PawnID) CarePawn {
		return CarePawn{ID: id, Dead: domain.Known(false), Conditions: domain.Known([]CareCondition{infection(7, 0.6, 0.3, 0.1, 0.1)}), Operations: domain.Known([]SurgeryOperation{amputate(7, 0.9)})}
	}
	_, patients := EmergencyNeeds(snapshot, current, 7)
	urgent := UrgentPatients(snapshot, current, 7)
	critical, pressing := AmputationNeeds(snapshot, domain.Known([]CarePawn{infected("infected"), infected("bleeding")}), patients, urgent)
	if critical != domain.Known(int64(2)) || pressing != domain.Known(int64(2)) {
		t.Fatalf("critical %v urgent %v", critical, pressing)
	}
	if c, u := AmputationNeeds(snapshot, domain.Unknown[[]CarePawn](), patients, urgent); c != patients || u != urgent {
		t.Fatalf("unknown care facts changed counts: %v %v", c, u)
	}
	if _, u := AmputationNeeds(snapshot, domain.Known([]CarePawn{infected("infected")}), patients, domain.Unknown[int64]()); known(u) {
		t.Fatal("unknown urgent count became known")
	}
}
