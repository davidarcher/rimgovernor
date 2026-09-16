package bridge

import (
	"context"

	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// gearReplaceJobDef is the native job RimWorld issues to wear an already
// carried or produced item; ImproveGear is the native operation behind
// home/gear_upkeep. This name is
// unverified against native source from this repo and remains an open native
// acceptance item for G01.12.
const gearReplaceJobDef = "Wear"

type GearReplaceAttempt struct {
	Identity     *c.Identity
	Attempt      *c.AttemptKey
	Generation   uint64
	Pawn, Thing  string
	PawnToken    string
	ThingToken   string
	LoadoutToken string
}

func gearEntity(id, token string) *o.EntityPrecondition {
	return &o.EntityPrecondition{EntityId: proto.String(id), ExpectedSnapshotToken: proto.String(token)}
}
func gearReplaceOperation(pawn, pawnToken, thing, thingToken, loadoutToken string) *o.Operation {
	return &o.Operation{Command: &o.Operation_ImproveGear{ImproveGear: &o.ImproveGear{Pawn: gearEntity(pawn, pawnToken), Target: gearEntity(thing, thingToken), ExpectedLoadoutToken: proto.String(loadoutToken)}}}
}
func gearReplaceCommand(pawn, pawnToken, thing, thingToken, loadoutToken string) error {
	if validID(pawn) != nil || validID(pawnToken) != nil || validID(thing) != nil || validID(thingToken) != nil || validID(loadoutToken) != nil || pawn == thing {
		return contract("invalid gear replace command")
	}
	return nil
}

// PreviewGearReplace checks an exact already-selected pawn/item wear order;
// acceptance is not authority.
func (client *Client) PreviewGearReplace(ctx context.Context, identity *c.Identity, pawn, pawnToken, thing, thingToken, loadoutToken string) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := gearReplaceCommand(pawn, pawnToken, thing, thingToken, loadoutToken); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: gearReplaceOperation(pawn, pawnToken, thing, thingToken, loadoutToken)}, reply)
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
			return reply, raw, contract("gear replace preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		job := value.Projected.GetJob()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || job == nil {
			err = contract("gear replace preview facts missing")
			break
		}
		expected := &r.JobEffect{PawnId: proto.String(pawn), JobDef: proto.String(gearReplaceJobDef), TargetA: &r.JobTarget{Target: &r.JobTarget_ThingId{ThingId: thing}}, CanTry: proto.Bool(value.GetAccepted()), Issued: proto.Bool(false), Verified: proto.Bool(false)}
		if !proto.Equal(job, expected) {
			err = contract("gear replace preview projection mismatch")
		}
	default:
		err = contract("gear replace preview outcome missing")
	}
	return reply, raw, err
}

func gearReplaceAttempt(v GearReplaceAttempt) (GearReplaceAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return GearReplaceAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return GearReplaceAttempt{}, err
	}
	if v.Generation == 0 {
		return GearReplaceAttempt{}, contract("gear replace admission owner or generation mismatch")
	}
	if err := gearReplaceCommand(v.Pawn, v.PawnToken, v.Thing, v.ThingToken, v.LoadoutToken); err != nil {
		return GearReplaceAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}
func gearReplaceEvidence(evidence *r.EffectEvidence, expected GearReplaceAttempt) (*r.JobEffect, error) {
	job := evidence.GetJob()
	if job == nil || job.PawnId == nil || job.GetPawnId() != expected.Pawn || job.TargetA == nil || job.TargetA.GetThingId() != expected.Thing {
		return nil, contract("gear replace pawn or target mismatch")
	}
	allowed := &r.JobEffect{PawnId: job.PawnId, JobId: job.JobId, JobDef: job.JobDef, TargetA: job.TargetA, CanTry: job.CanTry, Issued: job.Issued, Verified: job.Verified, VerifiedReason: job.VerifiedReason}
	if !proto.Equal(job, allowed) || job.JobDef == nil || job.GetJobDef() != gearReplaceJobDef {
		return nil, contract("gear replace effect fields missing or unsupported")
	}
	if job.VerifiedReason != nil && !diagnostic(job.VerifiedReason) {
		return nil, contract("gear replace verification reason invalid")
	}
	return job, nil
}
func gearReplaceReceipt(v *r.Receipt, expected GearReplaceAttempt) error {
	if v == nil || buildingUnknown(v) != nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("gear replace admission mismatch")
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.Generation, true); err != nil {
		return err
	}
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("gear replace applied missing")
		}
		_, err := gearReplaceEvidence(outcome.Applied.GetObserved(), expected)
		return err
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil {
			return contract("gear replace uncertainty missing")
		}
		if outcome.Uncertain.LastObserved != nil {
			_, err := gearReplaceEvidence(outcome.Uncertain.LastObserved, expected)
			return err
		}
		return nil
	default:
		return contract("unsupported gear replace receipt")
	}
}

type GearReplaceWriter struct{ client *Client }

func NewGearReplaceWriter(client *Client) (*GearReplaceWriter, error) {
	if client == nil {
		return nil, contract("gear replace client missing")
	}
	return &GearReplaceWriter{client}, nil
}

// ApplyGearReplace dispatches one already-admitted wear order.
func (writer *GearReplaceWriter) ApplyGearReplace(ctx context.Context, pre *a.WritePrecondition, pawn, pawnToken, thing, thingToken, loadoutToken string) (*o.ExecuteReply, Result, error) {
	if writer == nil || writer.client == nil || pre == nil || buildingUnknown(pre) != nil || ValidateIdentity(pre.Identity) != nil || buildingAttempt(pre.Attempt) != nil || pre.GetExpectedGeneration() == 0 {
		return nil, Result{}, contract("invalid gear replace execution")
	}
	if err := gearReplaceCommand(pawn, pawnToken, thing, thingToken, loadoutToken); err != nil {
		return nil, Result{}, err
	}
	expected, err := gearReplaceAttempt(GearReplaceAttempt{Identity: pre.Identity, Attempt: pre.Attempt, Generation: pre.GetExpectedGeneration(), Pawn: pawn, Thing: thing, PawnToken: pawnToken, ThingToken: thingToken, LoadoutToken: loadoutToken})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	reply := &o.ExecuteReply{}
	raw, err := writer.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: gearReplaceOperation(pawn, pawnToken, thing, thingToken, loadoutToken)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = gearReplaceReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("gear replace execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupGearReplace(ctx context.Context, w GearReplaceAttempt) (*r.LookupReply, Result, error) {
	expected, err := gearReplaceAttempt(w)
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
		err = gearReplaceReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("gear replace in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.Generation, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("gear replace unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("gear replace lookup outcome missing")
	}
	return reply, raw, err
}
func (client *Client) ObserveGearReplaceProgress(ctx context.Context, w GearReplaceAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := gearReplaceAttempt(w)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = gearReplaceReceipt(admitted, expected); err != nil {
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
		err = gearReplaceProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("gear replace progress outcome missing")
	}
	return reply, raw, err
}
func gearReplaceProgress(v *r.Progress, expected GearReplaceAttempt, admitted *r.Receipt) error {
	if v == nil || v.CompleteInspection == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("gear replace progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("gear replace progress predates admission")
	}
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("gear replace unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil || !v.GetCompleteInspection() {
			return contract("gear replace pending missing")
		}
		_, err := gearReplaceEvidence(outcome.Pending.Evidence, expected)
		return err
	case *r.Progress_Completed:
		if outcome.Completed == nil || !v.GetCompleteInspection() {
			return contract("gear replace completed missing")
		}
		_, err := gearReplaceEvidence(outcome.Completed.Evidence, expected)
		return err
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("gear replace absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || !v.GetCompleteInspection() || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("gear replace unsuccessful reason missing")
		}
		_, err := gearReplaceEvidence(outcome.Unsuccessful.Evidence, expected)
		return err
	default:
		return contract("gear replace progress state missing")
	}
}
