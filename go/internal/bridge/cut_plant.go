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

// CutPlant is the blight responder's operation half (#245): the CutPlant
// designation on one exact blighted plant through DesignateThing with
// THING_DESIGNATION_CUT_PLANT, the same CAS-bound designate/lookup/observe
// shape as the supply Allow.
type CutPlantTarget struct {
	Plant domain.CutPlant
	Token string
}
type CutPlantRead struct {
	Context *c.ObservationContext
	Targets []CutPlantTarget
}
type CutPlantAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Plant      domain.CutPlant
}
type CutPlantControl struct{ client *Client }

func NewCutPlantControl(client *Client) (*CutPlantControl, error) {
	if client == nil {
		return nil, contract("cut plant client missing")
	}
	return &CutPlantControl{client}, nil
}

// ReadBlightedPlants is the colony read's blighted_plants census (no
// planning section) narrowed to the plants not yet designated: what the
// blight planner proposes from and the executor re-reads at inspect. A
// blighted_plants issue is ErrUnavailable, never an empty census.
func (client *Client) ReadBlightedPlants(ctx context.Context, identity *c.Identity) (CutPlantRead, Result, error) {
	if ValidateIdentity(identity) != nil {
		return CutPlantRead{}, Result{}, contract("invalid cut plant scope")
	}
	reply, raw, err := client.ReadColonyFacts(ctx, identity, false, nil)
	if err != nil {
		return CutPlantRead{}, raw, err
	}
	v := reply.GetObserved()
	for _, issue := range v.Issues {
		if issue.GetField() == "blighted_plants" {
			return CutPlantRead{}, raw, ErrUnavailable
		}
	}
	out := CutPlantRead{Context: proto.Clone(v.Context).(*c.ObservationContext), Targets: []CutPlantTarget{}}
	for _, row := range v.BlightedPlants {
		plant := row.GetPlant()
		if row.GetDesignated() {
			continue
		}
		target, err := domain.NewCutPlant(plant.GetId(), plant.GetDefName(), domain.Cell{X: plant.GetPosition().GetX(), Z: plant.GetPosition().GetZ()})
		if err != nil {
			return CutPlantRead{}, raw, err
		}
		out.Targets = append(out.Targets, CutPlantTarget{target, plant.GetSnapshot().GetToken()})
	}
	return out, raw, nil
}
func cutPlantOperation(target CutPlantTarget) *op.Operation {
	return &op.Operation{Command: &op.Operation_DesignateThing{DesignateThing: &op.DesignateThing{Target: &op.EntityPrecondition{EntityId: proto.String(target.Plant.Plant()), ExpectedSnapshotToken: proto.String(target.Token)}, Designation: op.ThingDesignation_THING_DESIGNATION_CUT_PLANT.Enum()}}}
}
func validCutPlant(target CutPlantTarget) error {
	if _, err := domain.NewCutPlant(target.Plant.Plant(), target.Plant.Definition(), target.Plant.Cell()); err != nil {
		return err
	}
	return validID(target.Token)
}
func (client *Client) PreviewCutPlant(ctx context.Context, identity *c.Identity, target CutPlantTarget) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validCutPlant(target) != nil {
		return nil, Result{}, contract("invalid cut plant preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: cutPlantOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown cut plant preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || buildingContext(v.Context, identity, 0, false) != nil || cutPlantEffect(v.Projected, target.Plant, true) != nil {
		return nil, raw, contract("invalid cut plant preview evidence")
	}
	return reply, raw, nil
}
func (writer *CutPlantControl) DesignateCutPlant(ctx context.Context, pre *a.WritePrecondition, target CutPlantTarget) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validCutPlant(target) != nil {
		return nil, Result{}, contract("invalid cut plant execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: cutPlantOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown cut plant execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	// The runtime additionally compares full owner/direction against its admission.
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("cut plant owner mismatch")
	}
	err = cutPlantReceipt(v, CutPlantAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target.Plant})
	return reply, raw, err
}
func validCutPlantAttempt(w CutPlantAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid cut plant attempt")
	}
	_, err := domain.NewCutPlant(w.Plant.Plant(), w.Plant.Definition(), w.Plant.Cell())
	return err
}
func cutPlantEffect(v *r.EffectEvidence, plant domain.CutPlant, allowed bool) error {
	d := v.GetDesignation()
	if d == nil || d.Present == nil || d.GetPresent() != allowed || d.GetThingId() != plant.Plant() || d.GetResourceDef() != plant.Definition() || d.GetDesignationDef() != "CutPlant" || d.Cell == nil || d.Cell.X == nil || d.Cell.Z == nil || d.Cell.GetX() != plant.Cell().X || d.Cell.GetZ() != plant.Cell().Z {
		return contract("cut plant effect mismatch")
	}
	return nil
}
func cutPlantReceipt(v *r.Receipt, w CutPlantAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("cut plant admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return cutPlantEffect(out.Applied.GetObserved(), w.Plant, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("cut plant uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			return cutPlantEffect(out.Uncertain.LastObserved, w.Plant, true)
		}
		return nil
	default:
		return contract("unsupported cut plant receipt")
	}
}
func (client *Client) LookupCutPlant(ctx context.Context, w CutPlantAttempt) (*r.LookupReply, Result, error) {
	if err := validCutPlantAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown cut plant lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = cutPlantReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("cut plant in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("cut plant lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveCutPlant(ctx context.Context, w CutPlantAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validCutPlantAttempt(w) != nil || cutPlantReceipt(admitted, w) != nil {
		return nil, Result{}, contract("cut plant observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown cut plant progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("cut plant progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing cut plant uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete cut plant completion")
		}
		err = cutPlantEffect(out.Completed.GetEvidence(), w.Plant, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified cut plant failure")
		}
		err = cutPlantEffect(out.Unsuccessful.GetEvidence(), w.Plant, false)
	case *r.Progress_Pending:
		// The plant still stands with its designation: the cut is pending
		// ordinary plant-cutting work.
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete cut plant pending")
		}
		err = cutPlantEffect(out.Pending.GetEvidence(), w.Plant, true)
	default:
		err = contract("unsupported cut plant progress")
	}
	return reply, raw, err
}
