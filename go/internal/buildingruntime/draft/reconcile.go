package draft

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

func (b *DraftBoundary) ObserveDraft(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.DraftEvidence, error) {
	out := executor.DraftEvidence{StartedAt: b.clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if err := ctx.Err(); err != nil {
		return out, err
	}
	attempt, err := b.attempt(p)
	if err != nil {
		return out, err
	}
	out.Pawn = domain.PawnID(attempt.PawnID)
	if current.Validate() != nil || current.Native == 0 || !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	lookup, _, err := b.native.LookupDraftAttempt(ctx, attempt)
	if err != nil {
		return out, err
	}
	receipt := lookup.GetReceipt()
	if receipt != nil {
		if err = boundary.Admission(receipt, p, b.session); err != nil {
			return out, err
		}
	}
	reply, _, err := b.native.ObserveDraftProgress(ctx, attempt, receipt)
	if err != nil {
		return out, err
	}
	progress := reply.GetProgress()
	if progress == nil || !proto.Equal(progress.Attempt, attempt.Attempt) {
		return out, executor.ErrEvidence
	}
	actual, err := boundary.Context(progress.Context, current)
	if err != nil {
		return out, err
	}
	if progress.Context.GetTick() < int64(p.Tick) || (receipt != nil && progress.Context.GetTick() < receipt.AdmittedContext.GetTick()) {
		return out, executor.ErrEvidence
	}
	row, observed, err := b.pawnRead(ctx, attempt.PawnID, actual)
	if err != nil {
		return out, err
	}
	if observed.GetTick() < progress.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	out.Drafted = boundary.FactBool(row.Drafted)
	out.Complete = progress.CompleteInspection != nil && progress.GetCompleteInspection()
	out.Observation.Snapshot = actual
	out.Observation.Tick = domain.Tick(observed.GetTick())
	out.Observation.Causality = domain.AfterDispatch
	var job *r.JobEffect
	switch v := progress.Effect.(type) {
	case *r.Progress_Unknown:
		if v.Unknown == nil {
			return out, executor.ErrEvidence
		}
	case *r.Progress_Pending:
		if v.Pending == nil {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectPending
	case *r.Progress_Absent:
		if !out.Complete || v.Absent == nil || !boundary.ValidID(v.Absent.GetInspectionToken()) {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectAbsent
	case *r.Progress_Completed:
		if !out.Complete || v.Completed == nil {
			return out, executor.ErrEvidence
		}
		job = v.Completed.GetEvidence().GetJob()
		if job == nil || job.Verified == nil || !job.GetVerified() || job.Drafted == nil || !job.GetDrafted() || job.DraftClaimId == nil {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectCompleted
	case *r.Progress_Unsuccessful:
		if !out.Complete || v.Unsuccessful == nil {
			return out, executor.ErrEvidence
		}
		reason := map[r.UnsuccessfulReason]domain.UnsuccessfulReason{r.UnsuccessfulReason_UNSUCCESSFUL_REASON_NATIVE_FAILURE: domain.NativeFailure, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_CANCELLED: domain.NativeCancelled, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED: domain.NativeInterrupted, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_EXPIRED: domain.NativeExpired, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_TARGET_DEAD: domain.TargetDead, r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED: domain.OutcomeNotAchieved}[v.Unsuccessful.GetReason()]
		if reason == "" {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = reason
	default:
		return out, executor.ErrEvidence
	}
	// Only a receipt or completed original-attempt progress identifies a claim.
	original := boundary.ReceiptJob(receipt)
	if job != nil && original != nil && original.DraftClaimId != nil && job.GetDraftClaimId() != original.GetDraftClaimId() {
		return out, executor.ErrEvidence
	}
	if job == nil {
		job = original
	}
	if job != nil && job.DraftClaimId != nil {
		out.Claim, err = b.claim(p, job, row, observed)
		if err != nil {
			if !errors.Is(err, executor.ErrHeld) {
				return out, err
			}
			out.Claim, err = b.historicalClaim(p, receipt, row, observed)
			if err != nil {
				return out, err
			}
			// The original receipt proves past acquisition. New ownership evidence
			// contradicts completion as our currently owned draft.
			if out.Observation.Effect != domain.EffectUnsuccessful {
				out.Observation.Effect = domain.EffectUnknown
			}
		}
	}
	if out.Observation.Effect == domain.EffectCompleted {
		if _, known := out.Claim.Value(); !known {
			return out, executor.ErrHeld
		}
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	out.ObservedAt = b.clock.Now()
	return out, nil
}

func (b *DraftBoundary) InspectDraftCleanup(ctx context.Context, p executor.Placement, cleanup domain.DraftCleanup) (executor.DraftCleanupInspection, error) {
	out := executor.DraftCleanupInspection{StartedAt: b.clock.Now()}
	if err := ctx.Err(); err != nil {
		return out, err
	}
	attempt, err := b.attempt(p)
	if err != nil {
		return out, err
	}
	identity, _, err := b.native.Identity(ctx)
	if err != nil {
		return out, err
	}
	context := identity.GetLoaded().GetContext()
	if err = bridge.ValidateContext(context); err != nil {
		return out, err
	}
	current := p.Snapshot
	current.Colony = domain.ColonyID(context.Identity.GetColonyId())
	current.Load = domain.LoadID(context.Identity.GetLoadToken())
	current.Map = domain.MapID(context.Identity.GetMapId())
	current.Native = domain.NativeGeneration(context.GetNativeGeneration())
	if !boundary.World(current, p.Snapshot) {
		out.ScopeSupersession = &domain.DraftScopeSupersession{Action: p.Action.ID(), Attempt: p.Attempt, Origin: p.Snapshot, Observed: current, Tick: domain.Tick(context.GetTick())}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	}
	claim, known := cleanup.Claim.Value()
	if !known {
		evidence, e := b.ObserveDraft(ctx, p, current)
		if e != nil {
			return out, e
		}
		out.Reconcile = &evidence
		out.ObservedAt = b.clock.Now()
		return out, nil
	}
	if claim.Action != p.Action.ID() || claim.Attempt != p.Attempt || claim.Pawn != domain.PawnID(attempt.PawnID) || claim.Origin != p.Snapshot || string(claim.Session) != b.session || !boundary.ValidID(string(claim.Claim)) {
		return out, executor.ErrEvidence
	}
	row, observed, err := b.pawnRead(ctx, attempt.PawnID, current)
	if err != nil {
		return out, err
	}
	if observed.GetTick() < context.GetTick() || observed.GetTick() < int64(p.Tick) {
		return out, executor.ErrEvidence
	}
	token, err := boundary.PawnToken(row, observed)
	if err != nil {
		return out, err
	}
	superseded := false
	switch state := row.GetDraftClaim().GetState().(type) {
	case *n.DraftClaimObservation_Unowned:
		if state.Unowned == nil {
			return out, executor.ErrHeld
		}
		superseded = true
	case *n.DraftClaimObservation_Owned:
		owned := state.Owned
		if owned == nil || !boundary.ValidID(owned.GetClaimId()) || !proto.Equal(owned.PawnSnapshot, row.Pawn.Snapshot) {
			return out, executor.ErrHeld
		}
		superseded = owned.GetClaimId() != string(claim.Claim)
	default:
		return out, executor.ErrHeld
	}
	if superseded {
		out.Supersession = &domain.DraftCleanupObservation{Claim: claim, Observed: current, Tick: domain.Tick(observed.GetTick()), Outcome: domain.DraftReleaseSuperseded}
	} else {
		if row.Drafted == nil || !row.GetDrafted() {
			return out, executor.ErrHeld
		}
		out.Request = &domain.DraftReleaseRequest{Claim: claim, PawnSnapshotToken: token, Observed: current, Tick: domain.Tick(observed.GetTick())}
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	out.ObservedAt = b.clock.Now()
	return out, nil
}

func (b *DraftBoundary) ReleaseDraft(ctx context.Context, release domain.DraftRelease) (executor.DraftCleanupReceipt, error) {
	out := executor.DraftCleanupReceipt{Release: release, Outcome: domain.DraftReleaseUncertain}
	q := release.Request
	claim := q.Claim
	if release.Sequence == 0 || q.Tick < 0 || q.Observed.Validate() != nil || q.Observed.Native == 0 || claim.Origin.Validate() != nil || claim.Origin.Native == 0 || claim.Attempt == 0 || !boundary.World(q.Observed, claim.Origin) || q.Observed.Native < claim.Origin.Native || !boundary.ValidID(string(claim.Action)) || !boundary.ValidID(string(claim.Pawn)) || !boundary.ValidID(string(claim.Claim)) || string(claim.Session) != b.session || !boundary.ValidID(q.PawnSnapshotToken) {
		return out, executor.ErrEvidence
	}
	request := &o.ReleaseOwnedDraftRequest{Identity: boundary.Identity(claim.Origin), Pawn: &o.EntityPrecondition{EntityId: proto.String(string(claim.Pawn)), ExpectedSnapshotToken: proto.String(q.PawnSnapshotToken)}, ExpectedClaimId: proto.String(string(claim.Claim))}
	if err := ctx.Err(); err != nil {
		return out, err
	}
	reply, _, err := b.cleanup.ReleaseOwnedDraft(ctx, request)
	if err != nil {
		return out, err
	}
	var result *o.DraftRelease
	issued := false
	switch v := reply.GetOutcome().(type) {
	case *o.ReleaseOwnedDraftReply_Released:
		result = v.Released
		issued = true
	case *o.ReleaseOwnedDraftReply_AlreadyReleased:
		result = v.AlreadyReleased
	case *o.ReleaseOwnedDraftReply_Uncertain:
		if v.Uncertain == nil || !proto.Equal(v.Uncertain.Request, request) || !validReleaseContext(v.Uncertain.Context, q) {
			return out, executor.ErrEvidence
		}
		return out, nil
	default:
		return out, executor.ErrEvidence
	}
	if result == nil || !proto.Equal(result.Request, request) || !validReleaseContext(result.Context, q) {
		return out, executor.ErrEvidence
	}
	job := result.Observed
	if job == nil || job.PawnId == nil || job.GetPawnId() != string(claim.Pawn) || job.Drafted == nil || job.GetDrafted() || job.Verified == nil || !job.GetVerified() || job.Issued == nil || job.GetIssued() != issued || job.GetDraftClaimId() != string(claim.Claim) || job.GetDraftOwner() != string(claim.Session) || !boundary.ValidID(job.GetResultingSnapshotToken()) {
		return out, executor.ErrEvidence
	}
	out.Outcome = domain.DraftReleaseConfirmed
	return out, nil
}
func validReleaseContext(v *c.ObservationContext, q domain.DraftReleaseRequest) bool {
	return bridge.ValidateContext(v) == nil && proto.Equal(v.Identity, boundary.Identity(q.Claim.Origin)) && v.NativeGeneration != nil && v.GetNativeGeneration() >= uint64(q.Observed.Native) && v.GetTick() >= int64(q.Tick)
}
