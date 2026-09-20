package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"slices"
)

type ApparelDefinition struct {
	Name                string
	Armor, Child, Adult bool
}
type ApparelPolicyState struct {
	Token           string
	Role            GearRoleInput
	Current         domain.ApparelPolicySpec
	ExcludesTainted bool
	Overrides       bool
	Definitions     []ApparelDefinition
}

// RoleApparelPolicy permits discovered stage-compatible definitions. Combat
// armor is reserved for soldiers; all roles exclude tainted and tattered gear.
func RoleApparelPolicy(pawn PawnID, role GearRole, state ApparelPolicyState) (domain.ApparelPolicy, bool) {
	s := domain.ApparelPolicySpec{Pawn: domain.PawnID(pawn), Token: state.Token, Name: "RimGovernor " + string(role), MinHP: .51, MaxHP: 1, MinQuality: 0, MaxQuality: 6}
	for _, d := range state.Definitions {
		if (role == GearChild && d.Child || role != GearChild && d.Adult) && (!d.Armor || role == GearSoldier) {
			s.Definitions = append(s.Definitions, d.Name)
		}
	}
	value, err := domain.NewApparelPolicy(s)
	if err != nil {
		return domain.ApparelPolicy{}, false
	}
	return value, true
}

func DesiredApparelPolicy(p GearPawn) (domain.ApparelPolicy, bool) {
	state, known := p.Policy.Value()
	if !known || p.Blocked {
		return domain.ApparelPolicy{}, false
	}
	role := DeriveGearRole(state.Role)
	if model, ok := p.LoadoutModel.Value(); ok {
		role = DeriveGearRole(model.Role)
	}
	desired, ok := RoleApparelPolicy(p.Pawn, role, state)
	if !ok {
		return domain.ApparelPolicy{}, false
	}
	spec := desired.Spec()
	current := state.Current
	defs := append([]string{}, current.Definitions...)
	slices.Sort(defs)
	if current.Name == spec.Name && slices.Equal(defs, spec.Definitions) && current.MinHP >= .50999 && current.MinHP <= .51001 && current.MaxHP == spec.MaxHP && current.MinQuality == spec.MinQuality && current.MaxQuality == spec.MaxQuality && state.ExcludesTainted && !state.Overrides {
		return domain.ApparelPolicy{}, false
	}
	return desired, true
}
