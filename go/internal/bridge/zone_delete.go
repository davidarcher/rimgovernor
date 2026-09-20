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

// ZoneDeleteAttempt is the write/lookup/observe scoping for one DeleteZone
// admission (#611), mirroring ClaimBuildingAttempt.
type ZoneDeleteAttempt struct {
	Identity   *c.Identity
	Attempt    *c.AttemptKey
	Generation uint64
	Delete     domain.ZoneDelete
}
type ZoneDeleteControl struct{ client *Client }

func NewZoneDeleteControl(client *Client) (*ZoneDeleteControl, error) {
	if client == nil {
		return nil, contract("zone delete client missing")
	}
	return &ZoneDeleteControl{client}, nil
}
func validateZoneDelete(t domain.ZoneDelete) error {
	_, err := domain.NewZoneDelete(t.Zone(), t.BeforeToken())
	return err
}
func zoneDeleteOperation(t domain.ZoneDelete) *op.Operation {
	return &op.Operation{Command: &op.Operation_DeleteZone{DeleteZone: &op.DeleteZone{
		Zone: &op.EntityPrecondition{EntityId: proto.String(t.Zone()), ExpectedSnapshotToken: proto.String(t.BeforeToken())},
	}}}
}
func (client *Client) PreviewZoneDelete(ctx context.Context, identity *c.Identity, target domain.ZoneDelete) (*op.PreviewReply, Result, error) {
	if ValidateIdentity(identity) != nil || validateZoneDelete(target) != nil {
		return nil, Result{}, contract("invalid zone delete preview")
	}
	reply := &op.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &op.PreviewRequest{Identity: proto.Clone(identity).(*c.Identity), Operation: zoneDeleteOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone delete preview fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetEvaluated()
	if v == nil || v.Accepted == nil || !v.GetAccepted() || v.Preparation != nil || v.Projected != nil || buildingContext(v.Context, identity, 0, false) != nil {
		return nil, raw, contract("invalid zone delete preview evidence")
	}
	return reply, raw, nil
}
func (writer *ZoneDeleteControl) ApplyZoneDelete(ctx context.Context, pre *a.WritePrecondition, target domain.ZoneDelete) (*op.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validateZoneDelete(target) != nil {
		return nil, Result{}, contract("invalid zone delete execution")
	}
	reply := &op.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &op.ExecuteRequest{Precondition: proto.Clone(pre).(*a.WritePrecondition), Operation: zoneDeleteOperation(target)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone delete execute fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetReceipt()
	if v == nil {
		return nil, raw, contract("zone delete owner mismatch")
	}
	err = zoneDeleteReceipt(v, ZoneDeleteAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), target})
	return reply, raw, err
}
func validZoneDeleteAttempt(w ZoneDeleteAttempt) error {
	if ValidateIdentity(w.Identity) != nil || buildingAttempt(w.Attempt) != nil || w.Generation == 0 {
		return contract("invalid zone delete attempt")
	}
	return validateZoneDelete(w.Delete)
}

// ZoneDeleted validates a DeleteZone effect against its target and reports
// whether the zone is gone: the native record carries the zone id, the
// before token and Present, with the cell readback only while the zone
// still stands (NativeZoneEditRecord.Evidence).
func ZoneDeleted(v *r.EffectEvidence, target domain.ZoneDelete) (bool, error) {
	d := v.GetZone()
	if d == nil || buildingUnknown(v) != nil || d.Snapshot == nil || d.GetZoneId() != target.Zone() || d.Snapshot.GetEntityId() != target.Zone() || d.Snapshot.GetBeforeToken() != target.BeforeToken() || d.Snapshot.AfterToken != nil || d.Present == nil {
		return false, contract("invalid zone delete readback")
	}
	if !d.GetPresent() {
		if d.ChangedCells != nil || d.ListedCellCount != nil || d.GridCellCount != nil || d.PhantomCellCount != nil || len(d.Cells) != 0 {
			return false, contract("deleted zone reports cells")
		}
		return true, nil
	}
	if d.ListedCellCount == nil || d.GridCellCount == nil || d.PhantomCellCount == nil || d.ChangedCells == nil || d.GetListedCellCount() != int32(len(d.Cells)) || d.GetGridCellCount() < 0 || d.GetPhantomCellCount() < 0 {
		return false, contract("invalid zone delete readback")
	}
	return false, nil
}
func zoneDeleteEffect(v *r.EffectEvidence, target domain.ZoneDelete, deleted bool) error {
	gone, err := ZoneDeleted(v, target)
	if err != nil {
		return err
	}
	if gone != deleted {
		return contract("zone delete effect mismatch")
	}
	return nil
}
func zoneDeleteReceipt(v *r.Receipt, w ZoneDeleteAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.AdmittedContext, w.Identity, w.Generation, true) != nil {
		return contract("zone delete admission mismatch")
	}
	switch out := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return zoneDeleteEffect(out.Applied.GetObserved(), w.Delete, true)
	case *r.Receipt_Uncertain:
		if out.Uncertain == nil {
			return contract("zone delete uncertainty missing")
		}
		if out.Uncertain.LastObserved != nil {
			_, err := ZoneDeleted(out.Uncertain.LastObserved, w.Delete)
			return err
		}
		return nil
	default:
		return contract("unsupported zone delete receipt")
	}
}
func (client *Client) LookupZoneDelete(ctx context.Context, w ZoneDeleteAttempt) (*r.LookupReply, Result, error) {
	if err := validZoneDeleteAttempt(w); err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone delete lookup fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = zoneDeleteReceipt(v.Receipt, w)
	case *r.LookupReply_Unknown:
		err = buildingContext(v.Unknown.GetContext(), w.Identity, 0, false)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, w.Attempt) {
			return nil, raw, contract("zone delete in-flight mismatch")
		}
		err = buildingContext(v.InFlight.AdmittedContext, w.Identity, w.Generation, true)
	default:
		err = contract("zone delete lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveZoneDelete(ctx context.Context, w ZoneDeleteAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	if validZoneDeleteAttempt(w) != nil || zoneDeleteReceipt(admitted, w) != nil {
		return nil, Result{}, contract("zone delete observation admission mismatch")
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: w.Identity, Attempt: w.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if buildingUnknown(reply) != nil {
		return nil, raw, contract("unknown zone delete progress fields")
	}
	if reply.GetFailure() != nil {
		return nil, raw, failure(reply.GetFailure(), raw)
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) || buildingContext(v.Context, w.Identity, 0, false) != nil || v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return nil, raw, contract("zone delete progress scope mismatch")
	}
	switch out := v.Effect.(type) {
	case *r.Progress_Unknown:
		if out.Unknown == nil {
			return nil, raw, contract("missing zone delete uncertainty")
		}
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() {
			return nil, raw, contract("incomplete zone delete completion")
		}
		err = zoneDeleteEffect(out.Completed.GetEvidence(), w.Delete, true)
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || out.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return nil, raw, contract("unverified zone delete failure")
		}
		err = zoneDeleteEffect(out.Unsuccessful.GetEvidence(), w.Delete, false)
	default:
		err = contract("unsupported zone delete progress")
	}
	return reply, raw, err
}
