package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

var recoveryServiceMethods = map[domain.RecoveryMethod]o.ServiceMethod{
	domain.RecoveryServiceRepair:    o.ServiceMethod_SERVICE_METHOD_REPAIR,
	domain.RecoveryServiceBreakdown: o.ServiceMethod_SERVICE_METHOD_BREAKDOWN,
	domain.RecoveryServiceRefuel:    o.ServiceMethod_SERVICE_METHOD_REFUEL,
}

// recoverAction is the RecoverIntent of one pawn, one colony building and
// one service method (repair, breakdown, refuel). Native checks the pawn,
// the building and whether it still needs the method live, and the game's
// own WorkGiver builds the job (NativeRecoveryOperations.cs).
func recoverAction(action domain.Action) (*o.Action, error) {
	v, ok := action.RecoveryService()
	if !ok {
		return nil, contract("not a recovery service action")
	}
	method, known := recoveryServiceMethods[v.Method()]
	if !known || validID(string(v.Pawn())) != nil || validID(v.Thing()) != nil {
		return nil, contract("recover intent requires a pawn, a building and a service method")
	}
	return &o.Action{Intent: &o.Action_Recover{Recover: &o.RecoverIntent{PawnId: proto.String(string(v.Pawn())), ThingId: proto.String(v.Thing()), Method: method.Enum()}}}, nil
}
