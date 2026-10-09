package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// removeProductionBillAction is the Actions/Apply remove_production_bill arm
// of one remove_production_bill action: native deletes the idle bill
// or refuses.
func removeProductionBillAction(action domain.Action) (*o.Action, error) {
	removal, ok := action.RemoveProductionBill()
	if !ok {
		return nil, contract("not a remove_production_bill action")
	}
	if _, err := domain.NewRemoveProductionBill(removal.Bench(), removal.Bill()); err != nil {
		return nil, contract("remove_production_bill: %v", err)
	}
	return &o.Action{Intent: &o.Action_RemoveProductionBill{RemoveProductionBill: &o.RemoveProductionBillIntent{
		BenchId: proto.String(removal.Bench()), Bill: &c.Ref{Id: proto.String(removal.Bill())}}}}, nil
}
