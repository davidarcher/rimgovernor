package bridge

import (
	"context"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type SupplyTarget struct {
	Supply domain.SupplyAllow
	Token  string
}
type SupplyRead struct {
	Context *c.ObservationContext
	Targets []SupplyTarget
}
type SupplyAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Supply     domain.SupplyAllow
}
type SupplyControl struct{ client *Client }

func NewSupplyControl(client *Client) (*SupplyControl, error) {
	if client == nil {
		return nil, contract("supply client missing")
	}
	return &SupplyControl{client}, nil
}
func (client *Client) ReadAllowSupplies(ctx context.Context, identity *c.Identity, cell domain.Cell) (SupplyRead, Result, error) {
	return client.readSupplyAccess(ctx, identity, cell, false)
}
func (client *Client) ReadForbidSupplies(ctx context.Context, identity *c.Identity, cell domain.Cell) (SupplyRead, Result, error) {
	return client.readSupplyAccess(ctx, identity, cell, true)
}
func (client *Client) readSupplyAccess(ctx context.Context, identity *c.Identity, cell domain.Cell, forbid bool) (SupplyRead, Result, error) {
	if ValidateIdentity(identity) != nil || cell.X < 0 || cell.Z < 0 {
		return SupplyRead{}, Result{}, contract("invalid supply scope")
	}
	point := &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)}
	request := &o.ListSuppliesRequest{Scope: &o.ReadScope{ExpectedIdentity: proto.Clone(identity).(*c.Identity)}, Filter: &o.StockFilter{Category: proto.String("haulable"), Ownership: proto.String("ours"), IncludeHeld: proto.Bool(false), ForbiddenOnly: proto.Bool(!forbid), Region: &o.Rectangle{Minimum: point, Maximum: proto.Clone(point).(*c.Cell)}}, Page: &c.PageRequest{Limit: proto.Uint32(256)}}
	reply := &o.ListSuppliesReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/observations_list_supplies", request, reply)
	if err != nil {
		return SupplyRead{}, raw, err
	}
	if reply.GetFailure() != nil {
		return SupplyRead{}, raw, failure(reply.GetFailure(), raw)
	}
	result, err := decodeSupplyAccess(reply, identity, cell, forbid)
	return result, raw, err
}
func decodeAllowSupplies(reply *o.ListSuppliesReply, identity *c.Identity, cell domain.Cell) (SupplyRead, error) {
	return decodeSupplyAccess(reply, identity, cell, false)
}
func decodeSupplyAccess(reply *o.ListSuppliesReply, identity *c.Identity, cell domain.Cell, forbid bool) (SupplyRead, error) {
	if reply == nil || buildingUnknown(reply) != nil {
		return SupplyRead{}, contract("invalid supply reply")
	}
	v := reply.GetObserved()
	if v == nil || buildingContext(v.Context, identity, 0, false) != nil || len(v.Stocks) > 256 {
		return SupplyRead{}, contract("supply census unavailable")
	}
	complete, err := emergencyCompleteness(v.Completeness, len(v.Stocks))
	if yes, known := complete.Value(); err != nil || !known || !yes {
		return SupplyRead{}, contract("incomplete supply census")
	}
	out := SupplyRead{Context: proto.Clone(v.Context).(*c.ObservationContext), Targets: []SupplyTarget{}}
	seen := map[string]bool{}
	for _, stock := range v.Stocks {
		if stock == nil || stock.Units == nil || stock.Forbidden == nil || stock.GetUnits() < 0 || stock.GetForbidden() < 0 || stock.GetForbidden() > stock.GetUnits() || !forbid && stock.GetForbidden() != stock.GetUnits() || len(stock.Items) > 256 {
			return SupplyRead{}, contract("invalid forbidden supply stock")
		}
		complete, err = emergencyCompleteness(stock.ItemsCompleteness, len(stock.Items))
		if yes, known := complete.Value(); err != nil || !known || !yes {
			return SupplyRead{}, contract("incomplete supply items")
		}
		for _, item := range stock.Items {
			if item == nil || validID(item.GetId()) != nil || seen[item.GetId()] || item.MapId == nil || item.GetMapId() != identity.GetMapId() || item.Position == nil || item.Position.X == nil || item.Position.Z == nil || item.Position.GetX() != cell.X || item.Position.GetZ() != cell.Z || item.GetDefName() != stock.GetDefinition().GetDefName() {
				return SupplyRead{}, contract("supply entity mismatch")
			}
			seen[item.GetId()] = true
			if len(seen) > 256 {
				return SupplyRead{}, contract("supply item census exceeds bound")
			}
			// Native issues distinguish ineligible items from an incomplete census.
			if item.Snapshot == nil {
				continue
			}
			if item.Snapshot.GetEntityId() != item.GetId() || !proto.Equal(item.Snapshot.Context, v.Context) || validID(item.Snapshot.GetToken()) != nil {
				return SupplyRead{}, contract("supply CAS scope mismatch")
			}
			supply, err := domain.NewSupplyAllow(item.GetId(), item.GetDefName(), cell)
			if err != nil {
				return SupplyRead{}, err
			}
			if forbid {
				supply, err = domain.NewSupplyForbid(item.GetId(), item.GetDefName(), cell)
				if err != nil {
					return SupplyRead{}, err
				}
			}
			out.Targets = append(out.Targets, SupplyTarget{supply, item.Snapshot.GetToken()})
			if len(out.Targets) > 256 {
				return SupplyRead{}, contract("supply targets exceed bound")
			}
		}
	}
	return out, nil
}
func supplyDesignation(s domain.SupplyAllow) *op.ThingDesignation {
	if s.Forbidden() {
		return op.ThingDesignation_THING_DESIGNATION_FORBID.Enum()
	}
	return op.ThingDesignation_THING_DESIGNATION_ALLOW.Enum()
}
func supplyOperation(target SupplyTarget) *op.Operation {
	return &op.Operation{Command: &op.Operation_DesignateThing{DesignateThing: &op.DesignateThing{Target: &op.EntityPrecondition{EntityId: proto.String(target.Supply.Thing()), ExpectedSnapshotToken: proto.String(target.Token)}, Designation: supplyDesignation(target.Supply)}}}
}
func validSupply(target SupplyTarget) error {
	if _, err := domain.NewSupplyAllow(target.Supply.Thing(), target.Supply.Definition(), target.Supply.Cell()); err != nil {
		return err
	}
	return validID(target.Token)
}
func (client *Client) PreviewSupplyAllow(ctx context.Context, identity *c.Identity, target SupplyTarget) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validSupply(target) != nil {
		return nil, Result{}, contract("invalid supply preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: supplyOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown supply preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || supplyEffect(v.Projected, target.Supply, true) != nil {
		return nil, raw, contract("invalid supply preview evidence")
	}
	return reply, raw, nil
}
func (writer *SupplyControl) AllowSupply(ctx context.Context, pre *a.WritePrecondition, target SupplyTarget) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validSupply(target) != nil {
		return nil, Result{}, contract("invalid supply execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: supplyOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown supply execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	// The runtime additionally compares full owner/direction against its admission.
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("supply owner mismatch")
	}
	err = supplyReceipt(v, SupplyAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target.Supply})
	return reply, raw, err
}
func validSupplyAttempt(w SupplyAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid supply attempt")
	}
	_, err := domain.NewSupplyAllow(w.Supply.Thing(), w.Supply.Definition(), w.Supply.Cell())
	return err
}
func supplyEffect(v *r.EffectEvidence, supply domain.SupplyAllow, allowed bool) error {
	d := v.GetDesignation()
	if d == nil || d.Present == nil || d.GetPresent() != allowed || d.GetThingId() != supply.Thing() || d.GetResourceDef() != supply.Definition() || d.GetDesignationDef() != supply.Designation() || d.Cell == nil || d.Cell.X == nil || d.Cell.Z == nil || d.Cell.GetX() != supply.Cell().X || d.Cell.GetZ() != supply.Cell().Z {
		return contract("supply effect mismatch")
	}
	return nil
}
func supplyReceipt(v *r.Receipt, w SupplyAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("supply admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return supplyEffect(out.Applied.GetObserved(), w.Supply, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("supply uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return supplyEffect(out.Uncertain.LastObserved, w.Supply, true)
		}
		return nil
	default:
		return contract("unsupported supply receipt")
	}
}
func (client *Client) LookupSupplyAllow(ctx context.Context, w SupplyAttempt) (*r.LookupReply, Result, error) {
	if err := validSupplyAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown supply lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = supplyReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("supply in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("supply lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveSupplyAllow(ctx context.Context, w SupplyAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validSupplyAttempt(w) != nil || supplyReceipt(admitted, w) != nil {
		return nil, Result{}, contract("supply observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown supply progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("supply progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing supply uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete supply completion")
		}
		err = supplyEffect(out.Completed.GetEvidence(), w.Supply, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified supply failure")
		}
		err = supplyEffect(out.Unsuccessful.GetEvidence(), w.Supply, false)
	default:
		err = contract("unsupported supply progress")
	}
	return reply, raw, err
}
