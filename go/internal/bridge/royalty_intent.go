package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// royaltyAction is the RoyaltyIntent of one royalty write (#1606): the
// colonist, the faction, the verb and the permit it takes; native applies the
// game's own checks (title, points, not yet held).
func royaltyAction(action domain.Action) (*o.Action, error) {
	royalty, ok := action.Royalty()
	if !ok {
		return nil, contract("not a royalty action")
	}
	if _, err := domain.NewRoyalty(royalty.Pawn(), royalty.Faction(), royalty.Verb(), royalty.Permit()); err != nil {
		return nil, contract("royalty requires a pawn, a faction, a known verb and a permit")
	}
	return &o.Action{Intent: &o.Action_Royalty{Royalty: &o.RoyaltyIntent{
		PawnId: proto.String(string(royalty.Pawn())), FactionDef: proto.String(royalty.Faction()),
		Verb: proto.String(string(royalty.Verb())), Permit: proto.String(royalty.Permit()),
	}}}, nil
}
