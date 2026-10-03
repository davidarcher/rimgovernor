package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// abilityAction is the AbilityIntent of one ability use (#1607): the pawn, the
// source arm and the target arm. Native's guard for the source checks
// eligibility against live state when it applies.
func abilityAction(action domain.Action) (*o.Action, error) {
	ability, ok := action.Ability()
	if !ok {
		return nil, contract("not an ability action")
	}
	// Rebuilding through the constructor re-runs the domain's source and
	// target-kind rules for a value assembled elsewhere.
	if _, err := domain.NewAbility(ability.Pawn(), ability.Source(), ability.Target()); err != nil {
		return nil, contract("ability: %v", err)
	}
	intent := &o.AbilityIntent{PawnId: proto.String(string(ability.Pawn()))}
	source := ability.Source()
	switch source.Kind() {
	case domain.AbilityPermit:
		intent.Source = &o.AbilityIntent_Permit{Permit: &o.PermitAbility{FactionDef: proto.String(source.Faction()), Permit: proto.String(source.Def())}}
	case domain.AbilityPsycast:
		intent.Source = &o.AbilityIntent_Psycast{Psycast: &o.PsycastAbility{Ability: proto.String(source.Def())}}
	default:
		return nil, contract("unsupported ability source %q", source.Kind())
	}
	target := ability.Target()
	switch target.Kind() {
	case domain.AbilityTargetNone:
		intent.Target = &o.AbilityIntent_NoTarget{NoTarget: &o.AbilityNoTarget{}}
	case domain.AbilityTargetCell:
		intent.Target = &o.AbilityIntent_Cell{Cell: &c.Cell{X: proto.Int32(target.Cell().X), Z: proto.Int32(target.Cell().Z)}}
	case domain.AbilityTargetPawn:
		intent.Target = &o.AbilityIntent_Pawn{Pawn: target.ID()}
	case domain.AbilityTargetThing:
		intent.Target = &o.AbilityIntent_Thing{Thing: target.ID()}
	default:
		return nil, contract("unsupported ability target %q", target.Kind())
	}
	return &o.Action{Intent: &o.Action_Ability{Ability: intent}}, nil
}
