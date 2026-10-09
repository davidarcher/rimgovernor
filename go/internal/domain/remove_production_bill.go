package domain

import "errors"

// RemoveProductionBillAction deletes one idle production bill from a bench:
// the bench and the native bill id a placing receipt
// journaled (Progress view Bill). Native re-resolves both live and refuses a
// bill that is gone, being worked or holding an unfinished item; Go does not
// restate those guards, and the Round retries.
const RemoveProductionBillAction ActionKind = "remove_production_bill"

// RemoveProductionBill is an immutable, comparable value.
type RemoveProductionBill struct{ bench, bill string }

func NewRemoveProductionBill(bench, bill string) (RemoveProductionBill, error) {
	if !validID(bench) || !validID(bill) {
		return RemoveProductionBill{}, errors.New("invalid production bill removal")
	}
	return RemoveProductionBill{bench: bench, bill: bill}, nil
}

func (r RemoveProductionBill) Bench() string { return r.bench }
func (r RemoveProductionBill) Bill() string  { return r.bill }

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
