package bridge

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	"google.golang.org/protobuf/proto"
)

// husbandryOrders maps each domain method to the order native applies
// (HusbandryActionHandler in NativeHusbandryOperations.cs).
var husbandryOrders = map[domain.HusbandryMethod]o.HusbandryOrder{
	domain.HusbandryTrain:           o.HusbandryOrder_HUSBANDRY_ORDER_TRAIN,
	domain.HusbandrySlaughter:       o.HusbandryOrder_HUSBANDRY_ORDER_SLAUGHTER,
	domain.HusbandryTame:            o.HusbandryOrder_HUSBANDRY_ORDER_TAME,
	domain.HusbandryRelease:         o.HusbandryOrder_HUSBANDRY_ORDER_RELEASE,
	domain.HusbandryAllowedArea:     o.HusbandryOrder_HUSBANDRY_ORDER_ALLOWED_AREA,
	domain.HusbandryMaster:          o.HusbandryOrder_HUSBANDRY_ORDER_MASTER,
	domain.HusbandryFollowDrafted:   o.HusbandryOrder_HUSBANDRY_ORDER_FOLLOW_DRAFTED,
	domain.HusbandryFollowFieldwork: o.HusbandryOrder_HUSBANDRY_ORDER_FOLLOW_FIELDWORK,
	domain.HusbandryCancelSlaughter: o.HusbandryOrder_HUSBANDRY_ORDER_CANCEL_SLAUGHTER,
	domain.HusbandryCancelRelease:   o.HusbandryOrder_HUSBANDRY_ORDER_CANCEL_RELEASE,
	domain.HusbandrySterilize:       o.HusbandryOrder_HUSBANDRY_ORDER_STERILIZE,
}

// husbandryAction is the HusbandryIntent of one animal order. Native checks
// the animal and the order's eligibility live when it applies; an order
// that already holds applies again.
func husbandryAction(action domain.Action) (*o.Action, error) {
	v, ok := action.Husbandry()
	if !ok {
		return nil, contract("not a husbandry action")
	}
	if v.Method() == domain.HusbandryPrioritizeSlaughter {
		return PrioritizedJob(domain.PawnID(v.Argument()), JobSlaughter, string(v.Animal()))
	}
	order, ok := husbandryOrders[v.Method()]
	if !ok || validID(string(v.Animal())) != nil {
		return nil, contract("invalid husbandry order")
	}
	intent := &o.HusbandryIntent{AnimalId: proto.String(string(v.Animal())), Order: &order}
	switch v.Method() {
	case domain.HusbandryTrain:
		intent.TrainableDef = proto.String(v.Argument())
	case domain.HusbandryAllowedArea, domain.HusbandryMaster:
		// An empty argument clears the assignment.
		if v.Argument() != "" {
			intent.TargetId = proto.String(v.Argument())
		}
	case domain.HusbandryFollowDrafted, domain.HusbandryFollowFieldwork:
		intent.Follow = proto.Bool(v.Argument() == "true")
	}
	return &o.Action{Intent: &o.Action_Husbandry{Husbandry: intent}}, nil
}
