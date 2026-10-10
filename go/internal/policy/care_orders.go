package policy

import "github.com/davidarcher/RimGovernor/go/internal/domain"

// The medical reserve, the surgery part bills, baby food and mech gestation
// declare the one bill they want to the work ledger (OrderDeclarer) and place
// nothing themselves. Their bills are declare-only in the first pass (#2604):
// the ledger places a missing one and keeps a matching one, but never removes
// one as an orphan, because a gestation bill is the mech being formed and a
// part or medicine bill serves a patient. What makes a bill declare-only is its
// recipe (LedgerBillKind, set by the bench census), not who placed it, so the
// guarantee holds across a restart and for a bill the ledger itself placed.

// SelectionOrder is a bill selection as the ledger's order spec on its bench's
// definition. ok is false when the selected bench is not in the readback or
// has no definition: the declaration is then unknown.
func SelectionOrder(s BillSelection, benches []GearBench) (OrderSpec, bool) {
	for _, b := range benches {
		if b.ID == s.Bench && b.Def != "" {
			return OrderSpec{Recipe: s.Recipe, Ingredients: s.Ingredients, Worker: s.Worker, Mode: s.Mode, Target: s.Target, BenchKind: b.Def}, true
		}
	}
	return OrderSpec{}, false
}

// DeclareSelection is a declare-only owner's declaration for one Round: the
// order of its selected bill, nothing when the selector chose none (a bill
// already standing makes its need, and the ledger keeps it), and an abstain when
// the selected bench cannot be named.
func DeclareSelection(s BillSelection, selected bool, benches []GearBench) Declared {
	if !selected {
		return Declared{}
	}
	spec, ok := SelectionOrder(s, benches)
	if !ok {
		return Abstaining(CauseUnreadBenchDef)
	}
	return Declared{Orders: []OrderSpec{spec}}
}

// DeclareMedicine is MaintainMedicalReserves' declaration: the stock-target
// medicine bill SelectMedicineMethod produces. Any other choice declares
// nothing (the planner harvests, waits or has no deficit), except an unknown
// choice, which abstains.
func DeclareMedicine(m MedicineMethod, benches []GearBench) Declared {
	switch m.Kind {
	case MedicineUnknown:
		return Abstaining(CauseUnreadMedicine)
	case MedicineProduce:
		return DeclareSelection(BillSelection{Bench: m.Bench, Recipe: m.Recipe, Mode: domain.StockTarget, Target: int32(m.Target)}, true, benches)
	}
	return Declared{}
}
