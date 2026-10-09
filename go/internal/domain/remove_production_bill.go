package domain

import "errors"

// RemoveProductionBillAction deletes one ordinary production bill from a bench:
// the bench and the native bill id a placing receipt journaled (Progress view
// Bill). Native re-resolves both live and refuses only a bill that is gone or
// not ordinary; a worked bill ends its job cleanly and an unfinished item
// survives for any same-recipe bill unless CancelUnfinished is set (a recipe
// change orphaning it). Go does not restate those guards, and the Round retries.
const RemoveProductionBillAction ActionKind = "remove_production_bill"

// RemoveProductionBill is an immutable, comparable value.
type RemoveProductionBill struct {
	bench, bill      string
	cancelUnfinished bool
}

func NewRemoveProductionBill(bench, bill string) (RemoveProductionBill, error) {
	if !validID(bench) || !validID(bill) {
		return RemoveProductionBill{}, errors.New("invalid production bill removal")
	}
	return RemoveProductionBill{bench: bench, bill: bill}, nil
}

func (r RemoveProductionBill) Bench() string { return r.bench }
func (r RemoveProductionBill) Bill() string  { return r.bill }

// CancelUnfinished returns the removal that also destroys the bill's bound
// unfinished item (DestroyMode.Cancel, returning its ingredient share).
func (r RemoveProductionBill) CancelUnfinished() RemoveProductionBill {
	r.cancelUnfinished = true
	return r
}

// CancelsUnfinished reports whether the bound unfinished item is destroyed.
func (r RemoveProductionBill) CancelsUnfinished() bool { return r.cancelUnfinished }

func NewRemoveProductionBillAction(id ActionID, r RemoveProductionBill) (Action, error) {
	if !validID(string(id)) {
		return Action{}, errors.New("invalid action identity")
	}
	if _, err := NewRemoveProductionBill(r.bench, r.bill); err != nil {
		return Action{}, err
	}
	return Action{id: id, kind: RemoveProductionBillAction, removeBill: r}, nil
}

func (a Action) RemoveProductionBill() (RemoveProductionBill, bool) {
	return a.removeBill, a.kind == RemoveProductionBillAction
}
