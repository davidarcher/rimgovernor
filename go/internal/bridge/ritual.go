package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// ritualAction is the RitualIntent of one ritual command (#1639, #1659): a
// start names the colonist the ritual is for, a begin the organizer, the
// ritual precept, the spot and the exact role and spectator assignments;
// native checks the ritual waits for the command, or may begin, when it
// applies.
func ritualAction(action domain.Action) (*o.Action, error) {
	ritual, ok := action.Ritual()
	if !ok {
		return nil, contract("not a ritual action")
	}
	intent := &o.RitualIntent{PawnId: proto.String(string(ritual.Pawn())), Ritual: proto.String(string(ritual.Ritual())), Verb: proto.String(string(ritual.Verb()))}
	switch ritual.Verb() {
	case domain.RitualStart:
		if _, err := domain.NewRitual(ritual.Pawn(), ritual.Ritual(), ritual.Verb()); err != nil {
			return nil, contract("ritual requires a pawn, a known ritual and a known verb")
		}
	case domain.RitualBegin:
		if _, err := domain.NewRitualBegin(ritual.Pawn(), string(ritual.Ritual()), ritual.Spot(), ritual.Slots(), ritual.Spectators()); err != nil {
			return nil, contract("ritual begin requires an organizer, a ritual precept, a spot and distinct assignments")
		}
		intent.Spot = &c.Cell{X: proto.Int32(ritual.Spot().X), Z: proto.Int32(ritual.Spot().Z)}
		for _, slot := range ritual.Slots() {
			assignment := &o.RitualRoleAssignment{Slot: proto.String(slot.Slot)}
			for _, pawn := range slot.Pawns {
				assignment.PawnIds = append(assignment.PawnIds, string(pawn))
			}
			intent.Roles = append(intent.Roles, assignment)
		}
		for _, pawn := range ritual.Spectators() {
			intent.SpectatorPawnIds = append(intent.SpectatorPawnIds, string(pawn))
		}
	default:
		return nil, contract("ritual requires a pawn, a known ritual and a known verb")
	}
	return &o.Action{Intent: &o.Action_Ritual{Ritual: intent}}, nil
}
