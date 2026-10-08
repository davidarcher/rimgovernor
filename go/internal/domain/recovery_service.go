package domain

import "errors"

// RecoveryMethod names the native service jobs an already-selected pawn can be
// sent to run on an already-selected building: ordinary repair, breakdown
// restoration, refuel and the void monolith's two orders. It mirrors
// policy.RecoveryMethod's string values; domain cannot import policy, so the
// two are kept in sync by convention and converted at the boundary.
type RecoveryMethod string

const (
	RecoveryServiceRepair    RecoveryMethod = "repair"
	RecoveryServiceBreakdown RecoveryMethod = "breakdown"
	RecoveryServiceRefuel    RecoveryMethod = "refuel"
	// The void monolith's two orders (#2437): the Inactive monolith is
	// investigated, every later level activated.
	RecoveryServiceInvestigateMonolith RecoveryMethod = "investigate_monolith"
	RecoveryServiceActivateMonolith    RecoveryMethod = "activate_monolith"
	// RecoveryServiceInteract is the awakening quest's interaction (#2438): a
	// void structure, the Gleaming monolith or the void node.
	RecoveryServiceInteract RecoveryMethod = "interact_thing"
)

// RecoveryService is explicit intent to send one already-observed undrafted
// pawn to repair, restore or refuel one already-observed building. It reuses
// a native GiveJobIntent service job (Actions/Apply). Native checks reachability and
// current job eligibility live when it applies.
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
	case RecoveryServiceRepair, RecoveryServiceBreakdown, RecoveryServiceRefuel, RecoveryServiceInvestigateMonolith, RecoveryServiceActivateMonolith, RecoveryServiceInteract:
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
