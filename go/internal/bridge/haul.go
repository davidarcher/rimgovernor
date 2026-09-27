package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// haulAction is the Actions/Apply haul arm of one domain haul action: the
// pawn and the item, nothing else. Native checks both against live state,
// lets the game's Hauling WorkGiver pick the storage, and applies a resend
// to a pawn already hauling the item without a new order
// (NativeHaulOperations.cs).
func haulAction(action domain.Action) (*o.Action, error) {
	h, ok := action.Haul()
	if !ok {
		return nil, contract("not a haul action")
	}
	pawn, thing := string(h.Pawn()), h.Thing()
	if validID(pawn) != nil || validID(thing) != nil || pawn == thing {
		return nil, contract("haul intent requires a distinct pawn and item")
	}
	return &o.Action{Intent: &o.Action_Haul{Haul: &o.HaulIntent{PawnId: proto.String(pawn), ThingId: proto.String(thing)}}}, nil
}
