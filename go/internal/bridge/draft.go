package bridge

import (
	"context"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func draftOperation(pawn *o.EntityPrecondition) *o.Operation {
	return &o.Operation{Command: &o.Operation_SetDrafted{SetDrafted: &o.SetDrafted{Pawn: pawn, Drafted: proto.Bool(true), AllowPersistentDraft: proto.Bool(false)}}}
}
func draftEntity(pawn *o.EntityPrecondition) error {
	if pawn == nil {
		return contract("draft pawn required")
	}
	if err := buildingUnknown(pawn); err != nil {
		return err
	}
	if err := validID(pawn.GetEntityId()); err != nil {
		return err
	}
	return validID(pawn.GetExpectedSnapshotToken())
}
func draftAttempt(v DraftAttempt) (DraftAttempt, error) {
	if err := ValidateIdentity(v.Identity); err != nil {
		return DraftAttempt{}, err
	}
	if err := buildingAttempt(v.Attempt); err != nil {
		return DraftAttempt{}, err
	}
	if v.NativeGeneration == 0 {
		return DraftAttempt{}, contract("draft admission owner or generation mismatch")
	}
	if err := validID(v.PawnID); err != nil {
		return DraftAttempt{}, err
	}
	v.Identity = proto.Clone(v.Identity).(*c.Identity)
	v.Attempt = proto.Clone(v.Attempt).(*c.AttemptKey)
	return v, nil
}

// PreviewDraft proposes only temporary drafted=true. Accepted is not authority.
func (client *Client) PreviewDraft(ctx context.Context, identity *c.Identity, pawn *o.EntityPrecondition) (*o.PreviewReply, Result, error) {
	if err := ValidateIdentity(identity); err != nil {
		return nil, Result{}, err
	}
	if err := draftEntity(pawn); err != nil {
		return nil, Result{}, err
	}
	identity = proto.Clone(identity).(*c.Identity)
	pawn = proto.Clone(pawn).(*o.EntityPrecondition)
	reply := &o.PreviewReply{}
	raw, err := client.protoRead(ctx, "rimgovernor/operations_preview", &o.PreviewRequest{Identity: identity, Operation: draftOperation(pawn)}, reply)
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
			return reply, raw, contract("draft preview missing")
		}
		if err = buildingContext(value.Context, identity, 0, false); err != nil {
			break
		}
		job := value.Projected.GetJob()
		if value.Accepted == nil || !diagnostic(value.Reason) || value.Preparation != nil || job == nil {
			err = contract("draft preview facts missing")
			break
		}
		expected := &r.JobEffect{PawnId: proto.String(pawn.GetEntityId()), Drafted: proto.Bool(true), CanTry: proto.Bool(value.GetAccepted()), Issued: proto.Bool(false), Verified: proto.Bool(false)}
		if !proto.Equal(job, expected) {
			err = contract("draft preview projection mismatch")
		}
	default:
		err = contract("draft preview outcome missing")
	}
	return reply, raw, err
}
func (control *DraftControl) DraftPawn(ctx context.Context, pre *a.WritePrecondition, pawn *o.EntityPrecondition) (*o.ExecuteReply, Result, error) {
	if control == nil || control.client == nil {
		return nil, Result{}, contract("draft capability missing")
	}
	if pre == nil {
		return nil, Result{}, contract("draft lease missing")
	}
	if err := buildingUnknown(pre); err != nil {
		return nil, Result{}, err
	}
	if err := draftEntity(pawn); err != nil {
		return nil, Result{}, err
	}
	expected, err := draftAttempt(DraftAttempt{pre.Identity, pre.Attempt, pre.GetExpectedGeneration(), pawn.GetEntityId()})
	if err != nil {
		return nil, Result{}, err
	}
	pre = proto.Clone(pre).(*a.WritePrecondition)
	pawn = proto.Clone(pawn).(*o.EntityPrecondition)
	reply := &o.ExecuteReply{}
	raw, err := control.client.protoCall(ctx, "rimgovernor/operations_execute", &o.ExecuteRequest{Precondition: pre, Operation: draftOperation(pawn)}, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ExecuteReply_Receipt:
		err = draftReceipt(v.Receipt, expected)
	case *o.ExecuteReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("draft execute outcome missing")
	}
	return reply, raw, err
}
func (client *Client) LookupDraftAttempt(ctx context.Context, attempt DraftAttempt) (*r.LookupReply, Result, error) {
	expected, err := draftAttempt(attempt)
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
		err = draftReceipt(v.Receipt, expected)
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, expected.Attempt) {
			err = contract("draft in-flight attempt mismatch")
		} else {
			err = buildingContext(v.InFlight.AdmittedContext, expected.Identity, expected.NativeGeneration, true)
		}
	case *r.LookupReply_Unknown:
		if v.Unknown == nil {
			err = contract("draft unknown context missing")
		} else {
			err = buildingContext(v.Unknown.Context, expected.Identity, 0, false)
		}
	case *r.LookupReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("draft lookup outcome missing")
	}
	return reply, raw, err
}

// ObserveDraftProgress never retries execution. Even a structurally valid completed
// reply must be paired by the runtime with fresh ReadPawns exact claim/full Owner
// evidence before binding, finishing or cleanup: JobEffect lacks owner direction.
func (client *Client) ObserveDraftProgress(ctx context.Context, attempt DraftAttempt, admitted *r.Receipt) (*r.ProgressReply, Result, error) {
	expected, err := draftAttempt(attempt)
	if err != nil {
		return nil, Result{}, err
	}
	if admitted != nil {
		if err = draftReceipt(admitted, expected); err != nil {
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
		err = draftProgress(v.Progress, expected, admitted)
	case *r.ProgressReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("draft progress outcome missing")
	}
	return reply, raw, err
}
func (cleanup *DraftCleanup) ReleaseOwnedDraft(ctx context.Context, request *o.ReleaseOwnedDraftRequest) (*o.ReleaseOwnedDraftReply, Result, error) {
	if cleanup == nil || cleanup.client == nil {
		return nil, Result{}, contract("draft cleanup capability missing")
	}
	if request == nil {
		return nil, Result{}, contract("draft release request missing")
	}
	if err := buildingUnknown(request); err != nil {
		return nil, Result{}, err
	}
	if err := ValidateIdentity(request.Identity); err != nil {
		return nil, Result{}, err
	}
	if err := draftEntity(request.Pawn); err != nil {
		return nil, Result{}, err
	}
	if err := validID(request.GetExpectedClaimId()); err != nil {
		return nil, Result{}, err
	}
	request = proto.Clone(request).(*o.ReleaseOwnedDraftRequest)
	reply := &o.ReleaseOwnedDraftReply{}
	raw, err := cleanup.client.protoCall(ctx, "rimgovernor/operations_release_owned_draft", request, reply)
	if err != nil {
		return nil, raw, err
	}
	if err = buildingUnknown(reply); err != nil {
		return reply, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.ReleaseOwnedDraftReply_Released:
		err = draftRelease(v.Released, request, true)
	case *o.ReleaseOwnedDraftReply_AlreadyReleased:
		err = draftRelease(v.AlreadyReleased, request, false)
	case *o.ReleaseOwnedDraftReply_Uncertain:
		if v.Uncertain == nil || !proto.Equal(v.Uncertain.Request, request) || !diagnostic(v.Uncertain.Detail) {
			err = contract("draft cleanup uncertainty mismatch")
		} else {
			err = buildingContext(v.Uncertain.Context, request.Identity, 0, false)
		}
	case *o.ReleaseOwnedDraftReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("draft release outcome missing")
	}
	return reply, raw, err
}
func draftReceipt(v *r.Receipt, expected DraftAttempt) error {
	if v == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("draft receipt attempt or owner mismatch")
	}
	if err := buildingUnknown(v); err != nil {
		return err
	}
	if err := buildingContext(v.AdmittedContext, expected.Identity, expected.NativeGeneration, true); err != nil {
		return err
	}
	var evidence *r.EffectEvidence
	complete := false
	issued := false
	switch outcome := v.Outcome.(type) {
	case *r.Receipt_Applied:
		if outcome.Applied == nil {
			return contract("draft applied missing")
		}
		evidence = outcome.Applied.Observed
		complete = true
		issued = true
	case *r.Receipt_NoChange:
		if outcome.NoChange == nil || !diagnostic(outcome.NoChange.Detail) {
			return contract("draft no-change missing")
		}
		evidence = outcome.NoChange.Observed
		complete = true
	case *r.Receipt_Uncertain:
		if outcome.Uncertain == nil || !diagnostic(outcome.Uncertain.Detail) {
			return contract("draft uncertainty missing")
		}
		evidence = outcome.Uncertain.LastObserved
	default:
		return contract("draft receipt outcome missing")
	}
	if evidence == nil && !complete {
		return nil
	}
	job, err := draftEvidence(evidence, expected.PawnID)
	if err != nil {
		return err
	}
	if complete && (job.Drafted == nil || !job.GetDrafted() || job.Verified == nil || !job.GetVerified() || job.Issued == nil || job.GetIssued() != issued || job.DraftClaimId == nil || job.ResultingSnapshotToken == nil) {
		return contract("verified owned draft facts missing")
	}
	return nil
}
func draftEvidence(evidence *r.EffectEvidence, pawn string) (*r.JobEffect, error) {
	job := evidence.GetJob()
	if job == nil || job.PawnId == nil || job.GetPawnId() != pawn {
		return nil, contract("draft pawn effect mismatch")
	}
	// Draft evidence cannot smuggle a different job, target or other operation.
	allowed := &r.JobEffect{PawnId: job.PawnId, Drafted: job.Drafted, Issued: job.Issued, Verified: job.Verified, VerifiedReason: job.VerifiedReason, ResultingSnapshotToken: job.ResultingSnapshotToken, DraftClaimId: job.DraftClaimId}
	if !proto.Equal(job, allowed) || !diagnostic(job.VerifiedReason) {
		return nil, contract("unexpected draft effect fields")
	}
	for _, id := range []*string{job.DraftClaimId, job.ResultingSnapshotToken} {
		if id != nil {
			if err := validID(*id); err != nil {
				return nil, err
			}
		}
	}
	return job, nil
}
func draftObserved(v *r.Receipt) *r.JobEffect {
	if v == nil {
		return nil
	}
	switch result := v.Outcome.(type) {
	case *r.Receipt_Applied:
		return result.Applied.GetObserved().GetJob()
	case *r.Receipt_NoChange:
		return result.NoChange.GetObserved().GetJob()
	case *r.Receipt_Uncertain:
		return result.Uncertain.GetLastObserved().GetJob()
	}
	return nil
}
func draftProgress(v *r.Progress, expected DraftAttempt, admitted *r.Receipt) error {
	if v == nil || !proto.Equal(v.Attempt, expected.Attempt) {
		return contract("draft progress attempt mismatch")
	}
	if err := buildingContext(v.Context, expected.Identity, 0, false); err != nil {
		return err
	}
	if admitted != nil && v.Context.GetTick() < admitted.AdmittedContext.GetTick() {
		return contract("draft progress predates admission")
	}
	var evidence *r.EffectEvidence
	terminal := false
	completed := false
	switch outcome := v.Effect.(type) {
	case *r.Progress_Unknown:
		if outcome.Unknown == nil || !diagnostic(outcome.Unknown.Reason) {
			return contract("draft unknown progress missing")
		}
		return nil
	case *r.Progress_Pending:
		if outcome.Pending == nil {
			return contract("draft pending missing")
		}
		evidence = outcome.Pending.Evidence
	case *r.Progress_Completed:
		if outcome.Completed == nil {
			return contract("draft completed missing")
		}
		evidence = outcome.Completed.Evidence
		terminal = true
		completed = true
	case *r.Progress_Absent:
		if outcome.Absent == nil || validID(outcome.Absent.GetInspectionToken()) != nil || !v.GetCompleteInspection() {
			return contract("draft absence lacks inspection")
		}
		return nil
	case *r.Progress_Unsuccessful:
		if outcome.Unsuccessful == nil || outcome.Unsuccessful.Reason == nil || outcome.Unsuccessful.GetReason() == 0 || outcome.Unsuccessful.GetReason().Descriptor().Values().ByNumber(outcome.Unsuccessful.GetReason().Number()) == nil || !diagnostic(outcome.Unsuccessful.Detail) {
			return contract("draft unsuccessful reason missing")
		}
		evidence = outcome.Unsuccessful.Evidence
		terminal = true
	default:
		return contract("draft progress state missing")
	}
	if terminal && !v.GetCompleteInspection() {
		return contract("draft terminal inspection missing")
	}
	if evidence == nil && !terminal {
		return nil
	}
	job, err := draftEvidence(evidence, expected.PawnID)
	if err != nil {
		return err
	}
	if completed && (job.Drafted == nil || !job.GetDrafted() || job.Verified == nil || !job.GetVerified() || job.DraftClaimId == nil || job.ResultingSnapshotToken == nil) {
		return contract("draft completion facts missing")
	}
	if original := draftObserved(admitted); original != nil && original.DraftClaimId != nil && job.DraftClaimId != nil && original.GetDraftClaimId() != job.GetDraftClaimId() {
		return contract("draft progress claim mismatch")
	}
	return nil
}
func draftRelease(v *o.DraftRelease, request *o.ReleaseOwnedDraftRequest, issued bool) error {
	if v == nil || !proto.Equal(v.Request, request) {
		return contract("draft release request mismatch")
	}
	if err := buildingContext(v.Context, request.Identity, 0, false); err != nil {
		return err
	}
	job, err := draftEvidence(&r.EffectEvidence{Effect: &r.EffectEvidence_Job{Job: v.Observed}}, request.Pawn.GetEntityId())
	if err != nil {
		return err
	}
	if job.Drafted == nil || job.GetDrafted() || job.Verified == nil || !job.GetVerified() || job.Issued == nil || job.GetIssued() != issued || job.GetDraftClaimId() != request.GetExpectedClaimId() || job.ResultingSnapshotToken == nil {
		return contract("draft release readback mismatch")
	}
	return nil
}
