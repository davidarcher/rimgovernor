package domain

import "errors"

// RecoveryMethod names the three native disaster-recovery service jobs an
// already-selected pawn can be sent to run on an already-selected building:
// ordinary repair, breakdown restoration and refuel. It mirrors
// policy.RecoveryMethod's string values; domain cannot import policy, so the
// two are kept in sync by convention and converted at the boundary.
type RecoveryMethod string

const (
	RecoveryServiceRepair    RecoveryMethod = "repair"
	RecoveryServiceBreakdown RecoveryMethod = "breakdown"
	RecoveryServiceRefuel    RecoveryMethod = "refuel"
)

// RecoveryService is explicit intent to send one already-observed undrafted
// pawn to repair, restore or refuel one already-observed building. It reuses
// the native RecoverService operation, the same one the legacy JSON
// home/recover_service tool drives. Native reachability and current job
// eligibility are established at inspection, not here.
type RecoveryService struct {
	pawn   PawnID
	thing  string
	method RecoveryMethod
}

func NewRecoveryService(pawn PawnID, thing string, method RecoveryMethod) (RecoveryService, error) {
	if !validID(string(pawn)) || !validID(thing) {
		return RecoveryService{}, errors.New("recovery service requires a valid pawn and thing identity")
	}
	switch method {
	case RecoveryServiceRepair, RecoveryServiceBreakdown, RecoveryServiceRefuel:
	default:
		return RecoveryService{}, errors.New("recovery service requires a valid method")
	}
	return RecoveryService{pawn: pawn, thing: thing, method: method}, nil
}

func (r RecoveryService) Pawn() PawnID           { return r.pawn }
func (r RecoveryService) Thing() string          { return r.thing }
func (r RecoveryService) Method() RecoveryMethod { return r.method }

func NewRecoveryServiceAction(id ActionID, service RecoveryService) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewRecoveryService(service.pawn, service.thing, service.method); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: RecoveryServiceAction, recoveryService: service}, nil
}

func (a Action) RecoveryService() (RecoveryService, bool) {
	return a.recoveryService, a.kind == RecoveryServiceAction
}
