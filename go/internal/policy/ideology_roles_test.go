package policy

import (
	"reflect"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func rolePawn(id string, ideo, role string, certainty float64, skills ...WorkSkill) WorkPawn {
	return WorkPawn{ID: PawnID(id), Available: domain.Known(true), Skills: domain.Known(skills),
		PolicyInputs: domain.Known(PawnPolicyInputs{Ideo: ideo, IdeoRole: role, IdeoCertainty: domain.Known(certainty)})}
}

func roleIdeology(roles ...HeldRole) domain.Fact[Ideoligion] {
	return domain.Known(Ideoligion{
		Defs: IdeologyDefs{Roles: map[string]RoleDef{
			"Leader":  {Name: "Leader", MaxCount: 1, Requirements: []RoleRequirement{{Skills: []SkillRequirement{{Skill: "Social", MinLevel: 6}}}}},
			"Shooter": {Name: "Shooter", MaxCount: 2, Requirements: []RoleRequirement{{}}},
		}},
		Facts: IdeoligionFacts{IdeoID: "Ideo_1", Roles: roles},
	})
}

func TestRoleAssignmentsPicksBestFitByCertaintyThenID(t *testing.T) {
	ideology := roleIdeology(HeldRole{ID: "Precept_1", Def: "Leader", Active: true})
	pawns := domain.Known([]WorkPawn{
		rolePawn("a", "Ideo_1", "", 0.5, WorkSkill{Name: "Social", Level: 8}),
		rolePawn("b", "Ideo_1", "", 0.9, WorkSkill{Name: "Social", Level: 8}),
		rolePawn("c", "Ideo_1", "", 1, WorkSkill{Name: "Social", Level: 5}),
		rolePawn("d", "Ideo_1", "", 1, WorkSkill{Name: "Social", Level: 12, Disabled: true}),
		rolePawn("e", "Ideo_2", "", 1, WorkSkill{Name: "Social", Level: 15}),
	})
	got, ok := RoleAssignments(ideology, pawns).Value()
	want := []RoleAssignment{{Pawn: "b", Role: "Precept_1", Def: "Leader"}}
	if !ok || !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v %v, want %v", got, ok, want)
	}
	if owed, _ := RolesOwed(ideology, pawns).Value(); !owed {
		t.Fatal("a fitting believer must owe the goal")
	}
}

func TestRoleAssignmentsSkipsFilledInactiveAndHolders(t *testing.T) {
	pawns := domain.Known([]WorkPawn{
		rolePawn("a", "Ideo_1", "Leader", 1, WorkSkill{Name: "Social", Level: 9}),
		rolePawn("b", "Ideo_1", "", 1, WorkSkill{Name: "Social", Level: 9}),
	})
	filled := roleIdeology(HeldRole{ID: "Precept_1", Def: "Leader", Active: true, Pawns: []domain.PawnID{"a"}})
	inactive := roleIdeology(HeldRole{ID: "Precept_1", Def: "Leader", Active: false})
	for name, ideology := range map[string]domain.Fact[Ideoligion]{"filled": filled, "inactive": inactive} {
		got, ok := RoleAssignments(ideology, pawns).Value()
		if !ok || len(got) != 0 {
			t.Fatalf("%s: got %v %v, want none", name, got, ok)
		}
		if owed, known := RolesOwed(ideology, pawns).Value(); !known || owed {
			t.Fatalf("%s: goal must be known and not owed", name)
		}
	}
}

func TestRoleAssignmentsOneRolePerPawnAndUnknown(t *testing.T) {
	ideology := roleIdeology(HeldRole{ID: "Precept_1", Def: "Leader", Active: true}, HeldRole{ID: "Precept_2", Def: "Shooter", Active: true})
	pawns := domain.Known([]WorkPawn{rolePawn("a", "Ideo_1", "", 1, WorkSkill{Name: "Social", Level: 9})})
	got, _ := RoleAssignments(ideology, pawns).Value()
	// The leader takes the only believer; the shooter role waits.
	want := []RoleAssignment{{Pawn: "a", Role: "Precept_1", Def: "Leader"}}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("got %v, want %v", got, want)
	}
	if _, ok := RoleAssignments(domain.Unknown[Ideoligion](), pawns).Value(); ok {
		t.Fatal("unknown ideology must stay unknown")
	}
	if _, ok := RoleAssignments(ideology, domain.Unknown[[]WorkPawn]()).Value(); ok {
		t.Fatal("unknown pawns must stay unknown")
	}
}

func TestRolesOwedRaisesMaintainIdeoRoles(t *testing.T) {
	f := stableRoutine()
	f.RolesOwed = domain.Known(true)
	for _, g := range needs(t, f, RoutineLatches{}).Goals {
		if g.ID == MaintainIdeoRoles {
			return
		}
	}
	t.Fatal("an owed role assignment raised no MaintainIdeoRoles goal")
}
