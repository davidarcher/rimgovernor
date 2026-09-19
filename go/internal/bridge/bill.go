package bridge

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type BillRead struct {
	Context *c.ObservationContext
	Token   string
}
type BillAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Bill       domain.ProductionBill
}
type BillControl struct{ client *Client }

func NewBillControl(client *Client) (*BillControl, error) {
	if client == nil {
		return nil, contract("bill client missing")
	}
	return &BillControl{client}, nil
}
func (client *Client) ReadBillTarget(ctx context.Context, identity *c.Identity, bench string) (BillRead, Result, error) {
	reply, raw, err := client.ReadColonyFacts(ctx, identity, false, nil)
	if err != nil {
		return BillRead{}, raw, err
	}
	var token string
	for _, row := range reply.GetObserved().Cooking {
		if row.Bench.GetId() == bench {
			token = row.Bench.GetSnapshot().GetToken()
		}
	}
	for _, row := range reply.GetObserved().Butchering {
		if row.Bench.GetId() == bench {
			if token != "" {
				return BillRead{}, raw, contract("duplicate bill bench")
			}
			token = row.Bench.GetSnapshot().GetToken()
		}
	}
	if token == "" {
		// Any other bill giver (a crafting spot, a stonecutter) lives only in the
		// generic bill-stack census, whose token is the same whole-stack hash.
		census, censusRaw, err := client.ReadGearBenches(ctx, identity)
		if err != nil {
			return BillRead{}, censusRaw, err
		}
		for _, row := range census {
			if row.Bench.ID == bench {
				token = row.Token
			}
		}
	}
	if validID(token) != nil {
		return BillRead{}, raw, ErrUnavailable
	}
	return BillRead{Context: proto.Clone(reply.GetObserved().Context).(*c.ObservationContext), Token: token}, raw, nil
}
func BillOperation(bill domain.ProductionBill) *op.Operation {
	settings := &op.BillSettings{Suspended: proto.Bool(false), IngredientSearchRadius: proto.Float32(40), Store: &op.BillStore{Destination: &op.BillStore_Mode{Mode: op.StoreMode_STORE_MODE_DROP_ON_FLOOR}}}
	if ingredients := bill.Ingredients(); len(ingredients) > 0 {
		selectors := make([]*op.FilterSelector, 0, len(ingredients))
		for _, name := range ingredients {
			selectors = append(selectors, &op.FilterSelector{Definition: &op.FilterSelector_ThingDef{ThingDef: name}})
		}
		settings.Ingredients = &op.FilterPatch{Replace: &op.SelectorList{Selectors: selectors}}
	}
	if bill.Mode() == domain.ButcherForever {
		settings.RepeatMode = op.RepeatMode_REPEAT_MODE_FOREVER.Enum()
	} else {
		settings.RepeatMode = op.RepeatMode_REPEAT_MODE_TARGET.Enum()
		settings.TargetCount = proto.Int32(bill.Target())
		settings.UnpauseThreshold = proto.Int32(max(1, bill.Target()/2))
		settings.PauseWhenSatisfied = proto.Bool(true)
	}
	return &op.Operation{Command: &op.Operation_AddBill{AddBill: &op.AddBill{Bench: &op.EntityPrecondition{EntityId: proto.String(bill.Bench()), ExpectedSnapshotToken: proto.String(bill.BeforeToken())}, RecipeDef: proto.String(bill.Recipe()), Settings: settings, ReplaceOwnedBillId: optionalReplacement(bill)}}}
}
func optionalReplacement(b domain.ProductionBill) *string {
	if b.Replaces() == "" {
		return nil
	}
	return proto.String(b.Replaces())
}
func validBill(bill domain.ProductionBill) error {
	_, err := domain.NewProductionBillAction("validate", bill)
	return err
}
func (client *Client) PreviewBill(ctx context.Context, identity *c.Identity, target domain.ProductionBill) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validBill(target) != nil {
		return nil, Result{}, contract("invalid bill preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: BillOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown bill preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || v.Projected != nil {
		return nil, raw, contract("invalid bill preview evidence")
	}
	return reply, raw, nil
}
func (writer *BillControl) AddBill(ctx context.Context, pre *a.WritePrecondition, target domain.ProductionBill) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validBill(target) != nil {
		return nil, Result{}, contract("invalid bill execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: BillOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown bill execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	// The runtime additionally compares full owner/direction against its admission.
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("bill owner mismatch")
	}
	err = billReceipt(v, BillAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Bill: target})
	return reply, raw, err
}
func validBillAttempt(w BillAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid bill attempt")
	}
	return validBill(w.Bill)
}
func ValidateBillEffect(v *r.EffectEvidence, bill domain.ProductionBill) error {
	d := v.GetBill()
	if d == nil || buildingUnknown(v) != nil || validBill(bill) != nil || d.Stack == nil || d.Stack.GetEntityId() != bill.Bench() || d.Stack.GetBeforeToken() != bill.BeforeToken() || validID(d.GetBillId()) != nil || d.GetRecipeDef() != bill.Recipe() || d.Present == nil || d.Index == nil || d.ConfigurationMatches == nil || d.Iterations == nil || d.GetIterations() > 1 || d.OutputComplete == nil || d.OutputObserved == nil || len(d.OrderedBillIds) > 15 || len(d.Outputs) > 256 {
		return contract("invalid production evidence")
	}
	ids := map[string]bool{}
	for _, id := range d.OrderedBillIds {
		if validID(id) != nil || ids[id] {
			return contract("invalid bill order")
		}
		ids[id] = true
	}
	if d.GetPresent() {
		if d.GetIndex() < 0 || int(d.GetIndex()) >= len(d.OrderedBillIds) || d.OrderedBillIds[d.GetIndex()] != d.GetBillId() || validID(d.Stack.GetAfterToken()) != nil {
			return contract("bill index/snapshot mismatch")
		}
	} else if d.GetIndex() != -1 || ids[d.GetBillId()] || d.GetConfigurationMatches() {
		return contract("absent bill contradiction")
	}
	outputs := map[string]bool{}
	for _, output := range d.Outputs {
		if output == nil || validID(output.GetThingId()) != nil || validID(output.GetDefName()) != nil || output.Units == nil || output.GetUnits() <= 0 || outputs[output.GetThingId()] {
			return contract("invalid production output")
		}
		outputs[output.GetThingId()] = true
	}
	if d.GetOutputObserved() && (!d.GetOutputComplete() || len(d.Outputs) == 0) || d.GetIterations() == 0 && len(d.Outputs) > 0 {
		return contract("unverified production output")
	}
	return nil
}
func billEffect(v *r.EffectEvidence, w BillAttempt) error { return ValidateBillEffect(v, w.Bill) }
func billReceipt(v *r.Receipt, w BillAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("bill admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return billEffect(out.Applied.GetObserved(), w)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("bill uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return billEffect(out.Uncertain.LastObserved, w)
		}
		return nil
	default:
		return contract("unsupported bill receipt")
	}
}
func (client *Client) LookupBill(ctx context.Context, w BillAttempt) (*r.LookupReply, Result, error) {
	if err := validBillAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown bill lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = billReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("bill in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("bill lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveBill(ctx context.Context, w BillAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validBillAttempt(w) != nil || billReceipt(admitted, w) != nil {
		return nil, Result{}, contract("bill observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown bill progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("bill progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing bill uncertainty")
		}
	case *r.Progress_Completed:
		err = ValidateBillEffect(out.Completed.GetEvidence(), w.Bill)
		d := out.Completed.GetEvidence().GetBill()
		if !v.GetCompleteInspection() || !d.GetConfigurationMatches() || d.GetIterations() == 0 || !d.GetOutputComplete() || !d.GetOutputObserved() || len(d.Outputs) == 0 {
			return nil, raw, contract("production not observed")
		}
	case *r.Progress_Unsuccessful:
		err = ValidateBillEffect(out.Unsuccessful.GetEvidence(), w.Bill)
		d := out.Unsuccessful.GetEvidence().GetBill()
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED || d.GetConfigurationMatches() && (d.GetIterations() == 0 || !d.GetOutputComplete() || len(d.Outputs) != 0) {
			return nil, raw, contract("unverified bill failure")
		}
	case *r.Progress_Pending:
		err = ValidateBillEffect(out.Pending.GetEvidence(), w.Bill)
		d := out.Pending.GetEvidence().GetBill()
		if !d.GetPresent() || !d.GetConfigurationMatches() || d.GetIterations() != 0 {
			return nil, raw, contract("invalid pending production")
		}

	default:
		err = contract("unsupported bill progress")
	}
	return reply, raw, err
}
