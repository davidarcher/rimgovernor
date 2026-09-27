package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// wasteAction is the WasteIntent of one pawn and one exposed waste item.
// Native checks the pawn, the item's protection and a separated destination
// live; the game's Hauling WorkGiver builds the job, and a pawn already
// hauling the item applies again (NativeWasteOperations.cs).
func wasteAction(action domain.Action) (*o.Action, error) {
	w, ok := action.Waste()
	if !ok {
		return nil, contract("not a waste action")
	}
	pawn, thing := string(w.Pawn()), w.Target()
	if validID(pawn) != nil || validID(thing) != nil || pawn == thing {
		return nil, contract("waste intent requires a distinct pawn and item")
	}
	return &o.Action{Intent: &o.Action_Waste{Waste: &o.WasteIntent{PawnId: proto.String(pawn), ThingId: proto.String(thing)}}}, nil
}
