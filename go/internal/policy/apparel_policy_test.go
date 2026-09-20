package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"reflect"
	"testing"
)

func TestRoleApparelPolicies(t *testing.T) {
	state := ApparelPolicyState{Token: "cas", Definitions: []ApparelDefinition{{Name: "Shirt", Adult: true}, {Name: "Armor", Armor: true, Adult: true}, {Name: "KidShirt", Child: true}, {Name: "Hat", Adult: true, Child: true}}}
	for _, tt := range []struct {
		role GearRole
		want []string
	}{{GearWorker, []string{"Hat", "Shirt"}}, {GearSoldier, []string{"Armor", "Hat", "Shirt"}}, {GearHunter, []string{"Hat", "Shirt"}}, {GearIndoor, []string{"Hat", "Shirt"}}, {GearChild, []string{"Hat", "KidShirt"}}, {GearSlave, []string{"Hat", "Shirt"}}, {GearNonCombatant, []string{"Hat", "Shirt"}}} {
		t.Run(string(tt.role), func(t *testing.T) {
			v, ok := RoleApparelPolicy("pawn", tt.role, state)
			s := v.Spec()
			if !ok || !reflect.DeepEqual(s.Definitions, tt.want) || s.MinHP != .51 || s.MaxHP != 1 || s.MinQuality != 0 || s.MaxQuality != 6 {
				t.Fatalf("policy: %+v", s)
			}
		})
	}
}

func TestApparelPoliciesOverridePlayerChoiceAndPreserveMatchingPolicy(t *testing.T) {
	state := ApparelPolicyState{Token: "cas", Definitions: []ApparelDefinition{{Name: "Shirt", Adult: true}}}
	p := GearPawn{Pawn: "p", Policy: domain.Known(state)}
	v, ok := DesiredApparelPolicy(p)
	if !ok {
		t.Fatal("player policy not replaced")
	}
	state.Current = v.Spec()
	state.Current.MinHP = float64(float32(.51))
	state.ExcludesTainted = true
	p.Policy = domain.Known(state)
	if _, ok := DesiredApparelPolicy(p); ok {
		t.Fatal("matching native policy repeated")
	}
	state.ExcludesTainted = false
	p.Policy = domain.Known(state)
	if _, ok := DesiredApparelPolicy(p); !ok {
		t.Fatal("tainted allowed was not repaired")
	}
	p.Blocked = true
	if _, ok := DesiredApparelPolicy(p); ok {
		t.Fatal("unavailable pawn was assigned")
	}
}
