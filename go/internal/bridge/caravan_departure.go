package bridge

import (
	"math"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// caravanDepartureAction is the FormCaravanIntent of one caravan departure:
// the crew, the whole pack by definition (journey food included)
// and the destination tile; native checks formation when it applies.
func caravanDepartureAction(action domain.Action) (*o.Action, error) {
	departure, ok := action.CaravanDeparture()
	if !ok {
		return nil, contract("not a caravan departure action")
	}
	crew := departure.Crew()
	if len(crew) == 0 || departure.DestinationTile() < 0 {
		return nil, contract("caravan departure requires a crew and a destination")
	}
	intent := &o.FormCaravanIntent{DestinationTile: proto.Int32(departure.DestinationTile())}
	for _, pawn := range crew {
		if validID(string(pawn)) != nil {
			return nil, contract("invalid caravan crew pawn")
		}
		intent.PawnIds = append(intent.PawnIds, string(pawn))
	}
	for _, item := range departure.Cargo() {
		if validID(item.Definition) != nil || item.Count == 0 || item.Count > math.MaxInt32 {
			return nil, contract("invalid caravan cargo item")
		}
		intent.Cargo = append(intent.Cargo, &o.DefCount{DefName: proto.String(item.Definition), Count: proto.Int32(int32(item.Count))})
	}
	return &o.Action{Intent: &o.Action_FormCaravan{FormCaravan: intent}}, nil
}
