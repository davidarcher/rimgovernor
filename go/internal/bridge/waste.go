package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// wasteJobDefs lists the native job defs ManageWaste's own native hauling
// WorkGiver scan may produce: relocation to a dirty outdoor stockpile cell,
// or burial in an empty grave. The native contract is
// NativeWasteOperations.cs (integrations/rimgovernor-native/src/Bridge/Protocol),
// wired onto Operation_ManageWaste in NativeOperationTools.cs's Execute/Preview
// dispatch. It ports the legacy JSON home/manage_waste tool's
// (HomeWasteTools.Haul) eligibility, protection and destination checks
// behind the typed boundary; an accepted job does not prove containment.
var wasteJobDefs = map[string]bool{"HaulToCell": true, "HaulToContainer": true}

const maxWasteIDs = 256

type WasteAttempt struct {
	Identity             *c.Identity
	Attempt              *c.AttemptKey
	Owner                *a.Owner
	Generation           uint64
	Pawn, PawnToken      string
	Target, TargetToken  string
	UnwantedIDs, BuryIDs []string
}

func wasteOperation(pawn, pawnToken, target, targetToken string, unwanted, bury []string) *o.Operation {
	return &o.Operation{Command: &o.Operation_ManageWaste{ManageWaste: &o.ManageWaste{
		Target: gearEntity(target, targetToken), Pawn: gearEntity(pawn, pawnToken), UnwantedIds: unwanted, BuryIds: bury,
	}}}
}

func wasteIDList(ids []string) error {
	if len(ids) > maxWasteIDs {
		return contract("waste id list exceeds bound")
	}
	seen := map[string]bool{}
	for _, id := range ids {
		if validID(id) != nil || seen[id] {
			return contract("invalid or duplicate waste id")
		}
		seen[id] = true
	}
	return nil
}

func wasteCommand(pawn, pawnToken, target, targetToken string, unwanted, bury []string) error {
	if validID(pawn) != nil || validID(pawnToken) != nil || validID(target) != nil || validID(targetToken) != nil || pawn == target {
		return contract("invalid waste command")
	}
	if err := wasteIDList(unwanted); err != nil {
		return err
	}
	if err := wasteIDList(bury); err != nil {
		return err
	}
	return nil
}

// PreviewWaste checks an exact already-selected pawn/waste item haul or
// burial order; acceptance is not authority.
func (client *Client) PreviewWaste(ctx context.Context, identity *c.Identity, pawn, pawnToken, target, targetToken string, unwanted, bury []string) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := wasteCommand(pawn, pawnToken, target, targetToken, unwanted, bury); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: wasteOperation(pawn, pawnToken, target, targetToken, unwanted, bury)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.PreviewReply_Failure:
		err = failure(v.Failure, raw)
	case *o.PreviewReply_Evaluated:
		value := v.Evaluated
		if value == nil {
			return reply, raw, contract("waste preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		job := value.Projected.GetJob()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || job == nil {
			err = contract("waste preview facts missing")
			break
		}
		expected := &r.JobEffect{PawnId: proto.String(pawn), JobDef: job.JobDef, TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: target}}, CanTry: proto.Bool(value.GetAccepted()), Issued: proto.Bool(false), Verified: proto.Bool(false)}
		if job.JobDef == nil || !wasteJobDefs[job.GetJobDef()] || !proto.Equal(job, expected) {
			err = contract("waste preview projection mismatch")
		}
	default:
		err = contract("waste preview outcome missing")
	}
	return reply, raw, err
}

func wasteAttempt(v WasteAttempt) (WasteAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return WasteAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return WasteAttempt{}, err
	}
	if err := authorityOwner(v.Owner); err != nil {
		return WasteAttempt{}, err
	}
	if err := buildingUnknown(v.Owner); err != nil {
		return WasteAttempt{}, err
	}
	if v.Generation == 0 || v.Owner.GetControllerSessionId() != v.Attempt.GetControllerSessionId() {
		return WasteAttempt{}, contract("waste admission owner or generation mismatch")
	}
	if err := wasteCommand(v.Pawn, v.PawnToken, v.Target, v.TargetToken, v.UnwantedIDs, v.BuryIDs); err != nil {
		return WasteAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	v.Owner = proto.Clone(v.Owner).(*a.Owner)
	return v, nil
}

func wasteEvidence(evidence *r.EffectEvidence, expected WasteAttempt) (*r.JobEffect, error) {
	job := evidence.GetJob()
	if job == nil || job.PawnId == nil || job.GetPawnId() != expected.Pawn || job.TargetA == nil || job.TargetA.GetThingId() != expected.Target {
		return nil, contract("waste pawn or target mismatch")
	}
	allowed := &r.JobEffect{PawnId: job.PawnId, JobId: job.JobId, JobDef: job.JobDef, TargetA: job.TargetA, CanTry: job.CanTry, Issued: job.Issued, Verified: job.Verified, VerifiedReason: job.VerifiedReason}
	if !proto.Equal(job, allowed) || job.JobDef == nil || !wasteJobDefs[job.GetJobDef()] {
		return nil, contract("waste effect fields missing or unsupported")
	}
	if job.VerifiedReason != nil && !diagnostic(job.VerifiedReason) {
		return nil, contract("waste verification reason invalid")
	}
	return job, nil
}

func wasteReceipt(v *r.Receipt, expected WasteAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) || !proto.Equal(v.AuthorizingOwner, expected.Owner) {
		return contract("waste admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("waste applied missing")
		}
		_, err := wasteEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("waste uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := wasteEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported waste receipt")
	}
}

type WasteWriter struct{ client *Client }

func NewWasteWriter(client *Client) (*WasteWriter, error) {
	if client == nil {
		return nil, contract("waste client missing")
	}
	return &WasteWriter{client}, nil
}

// ApplyWaste dispatches one already-admitted waste relocation or burial haul.
func (writer *WasteWriter) ApplyWaste(ctx context.Context, pre *a.WritePrecondition, owner *a.Owner, pawn, pawnToken, target, targetToken string, unwanted, bury []string) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 || validID(pre.GetLeaseId()) != nil {
		return nil, Result{}, contract("invalid waste execution")
	}
	if err := wasteCommand(pawn, pawnToken, target, targetToken, unwanted, bury); err != nil {
		return nil, Result{}, err
	}
	expected, err := wasteAttempt(WasteAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Owner: owner, Generation: pre.GetExpectedGeneration(), Pawn: pawn, PawnToken: pawnToken, Target: target, TargetToken: targetToken, UnwantedIDs: unwanted, BuryIDs: bury})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: wasteOperation(pawn, pawnToken, target, targetToken, unwanted, bury)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = wasteReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("waste execute outcome missing")
	}
	return reply, raw, err
}

func (client *Client) LookupWaste(ctx context.Context, w WasteAttempt) (*r.LookupReply, Result, error) {
	expected, err := wasteAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	reply := &r.LookupReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_lookup", &r.LookupRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Receipt:
		err = wasteReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("waste in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("waste unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("waste lookup outcome missing")
	}
	return reply, raw, err
}

func (client *Client) ObserveWasteProgress(ctx context.Context, w WasteAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := wasteAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = wasteReceipt(admitted, expected); err != nil {
			return nil, Result{}, err
		}
		admitted = proto.Clone(admitted).(*r.Receipt)
	}
	reply := &r.ProgressReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/receipts_observe_progress", &r.ProgressRequest{Identity: expected.Identity, Attempt: expected.Attempt}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *r.ProgressReply_Progress:
		err = wasteProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("waste progress outcome missing")
	}
	return reply, raw, err
}

func wasteProgress(v *r.Progress, expected WasteAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("waste progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("waste progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("waste unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("waste pending missing")
		}
		_, err := wasteEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("waste completed missing")
		}
		_, err := wasteEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("waste absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("waste unsuccessful reason missing")
		}
		_, err := wasteEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("waste progress state missing")
	}
}
