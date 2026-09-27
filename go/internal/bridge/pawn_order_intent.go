package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// pawnOrderIntent is the PawnOrderIntent of one pawn, one target and one
// order kind. Native checks both live and builds the game's own job; a pawn
// already running it on the target applies again.
func pawnOrderIntent(pawn domain.PawnID, target string, kind o.PawnOrderKind) (*o.Action, error) {
	if validID(string(pawn)) != nil || validID(target) != nil || string(pawn) == target {
		return nil, contract("pawn order intent requires a distinct pawn and target")
	}
	return &o.Action{Intent: &o.Action_PawnOrder{PawnOrder: &o.PawnOrderIntent{
		PawnId: proto.String(string(pawn)), TargetId: proto.String(target), Kind: kind.Enum()}}}, nil
}

func repairAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Repair()
	if !ok {
		return nil, contract("not a repair action")
	}
	return pawnOrderIntent(v.Pawn(), v.Structure(), o.PawnOrderKind_PAWN_ORDER_KIND_REPAIR)
}

func cleanAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Clean()
	if !ok {
		return nil, contract("not a clean action")
	}
	return pawnOrderIntent(v.Pawn(), v.Filth(), o.PawnOrderKind_PAWN_ORDER_KIND_CLEAN)
}

func openCasketAction(action domain.Action) (*o.Action, error) {
	v, ok := action.OpenCasket()
	if !ok {
		return nil, contract("not an open casket action")
	}
	return pawnOrderIntent(v.Pawn(), v.Casket(), o.PawnOrderKind_PAWN_ORDER_KIND_OPEN_CASKET)
}

func tendAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Tend()
	if !ok {
		return nil, contract("not a tend action")
	}
	return pawnOrderIntent(v.Doctor(), string(v.Patient()), o.PawnOrderKind_PAWN_ORDER_KIND_TEND)
}
