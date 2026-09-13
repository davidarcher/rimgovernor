package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	n "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// recoveryServiceMethodWire converts the domain-level method (kept in sync by
// convention with policy.RecoveryMethod, see domain.RecoveryMethod) to the
// bridge's native enum.
func recoveryServiceMethodWire(method domain.RecoveryMethod) bridge.RecoveryServiceMethod {
	switch method {
	case domain.RecoveryServiceRepair:
		return bridge.RecoveryServiceRepair
	case domain.RecoveryServiceBreakdown:
		return bridge.RecoveryServiceBreakdown
	case domain.RecoveryServiceRefuel:
		return bridge.RecoveryServiceRefuel
	default:
		return bridge.RecoveryServiceUnspecified
	}
}

// RecoveryServiceNative reuses the generic bridge.ReadPawns for pawn
// eligibility facts (same shape as GearReplaceNative's) plus the existing
// bridge.ReadRepairTarget for the building's CAS token, the same scoped
// refresh Repair's PawnOrder path uses; no dedicated recovery-service read is
// needed to plan a method, only to refresh CAS tokens immediately before
// dispatch.
type RecoveryServiceNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadRepairTarget(context.Context, *c.Identity, string) (bridge.RepairTarget, bridge.Result, error)
	PreviewRecoveryService(context.Context, *c.Identity, string, string, string, string, bridge.RecoveryServiceMethod) (*o.PreviewReply, bridge.Result, error)
	LookupRecoveryService(context.Context, bridge.RecoveryServiceAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveRecoveryServiceProgress(context.Context, bridge.RecoveryServiceAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type RecoveryServiceWriter interface {
	ApplyRecoveryService(context.Context, *a.WritePrecondition, *a.Owner, string, string, string, string, bridge.RecoveryServiceMethod) (*o.ExecuteReply, bridge.Result, error)
}
type RecoveryServiceCapabilities struct {
	Native RecoveryServiceNative
	Writer RecoveryServiceWriter
}
type RecoveryServiceBoundary struct {
	native  RecoveryServiceNative
	writer  RecoveryServiceWriter
	leases  LeaseSource
	clock   executor.Clock
	session string
}

func NewRecoveryServiceBoundary(native RecoveryServiceNative, writer RecoveryServiceWriter, leases LeaseSource, clock executor.Clock, session string) (*RecoveryServiceBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundaryID(session) {
		return nil, errors.New("invalid recovery service boundary dependencies")
	}
	return &RecoveryServiceBoundary{native, writer, leases, clock, session}, nil
}

func (b *RecoveryServiceBoundary) InspectRecoveryService(ctx context.Context, target executor.Target) (executor.RecoveryServiceInspection, error) {
	out := executor.RecoveryServiceInspection{StartedAt: b.clock.Now()}
	service, ok := target.Action.RecoveryService()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundaryIdentity(target.Snapshot), []string{string(service.Pawn())})
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return out, executor.ErrHeld
	}
	current, err := boundaryContext(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != 1 || counts.GetReturned() != 1 || len(observed.Pawns) != 1 {
		return out, executor.ErrHeld
	}
	row := observed.Pawns[0]
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(service.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := draftToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	building, _, err := b.native.ReadRepairTarget(ctx, boundaryIdentity(current), service.Thing())
	if err != nil {
		return out, err
	}
	if _, err = boundaryContext(building.Context, current); err != nil {
		return out, err
	}
	if building.Context.GetTick() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	method := recoveryServiceMethodWire(service.Method())
	preview, _, err := b.native.PreviewRecoveryService(ctx, boundaryIdentity(current), string(service.Pawn()), pawnToken, service.Thing(), building.Token, method)
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundaryContext(evaluated.Context, current); err != nil {
		return out, err
	}
	job := evaluated.GetProjected().GetJob()
	if evaluated.Context.GetTick() < building.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(service.Pawn()) || job.GetTargetA().GetThingId() != service.Thing() || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	facts := policy.RecoveryServiceFacts{Snapshot: current, PawnTick: domain.Tick(building.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), ThingSnapshotToken: building.Token, NativeCanTry: draftBool(job.CanTry)}
	facts.Pawn = recoveryServicePawnFacts(service.Pawn(), row, pawnToken)
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func recoveryServicePawnFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.RecoveryServicePawnFacts {
	facts := policy.RecoveryServicePawnFacts{Pawn: pawn, SnapshotToken: token, Dead: draftBool(row.Dead), Downed: draftBool(row.Downed), Drafted: draftBool(row.Drafted), MentalState: draftPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !tendIssue(row.Job.Issues, "player_forced") && !tendIssue(row.Job.Issues, "queued_jobs") && !tendIssue(row.Job.Issues, "def_name") {
		facts.PlayerForced, facts.QueuedJobs = draftBool(row.Job.PlayerForced), draftUint(row.Job.QueuedJobs)
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}

func (b *RecoveryServiceBoundary) attempt(dispatch executor.RecoveryServiceDispatch) (bridge.RecoveryServiceAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	service, ok := p.Action.RecoveryService()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != service.Pawn() || admission.Thing != service.Thing() || admission.Method != service.Method() || admission.Tick > p.Tick || !boundaryID(admission.PawnSnapshotToken) || !boundaryID(admission.ThingSnapshotToken) {
		return bridge.RecoveryServiceAttempt{}, executor.ErrEvidence
	}
	return bridge.RecoveryServiceAttempt{Identity: boundaryIdentity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, Generation: uint64(p.Snapshot.Native), Pawn: string(service.Pawn()), Thing: service.Thing(), PawnToken: admission.PawnSnapshotToken, ThingToken: admission.ThingSnapshotToken, Method: recoveryServiceMethodWire(admission.Method)}, nil
}

func recoveryServiceJob(job *r.JobEffect, dispatch executor.RecoveryServiceDispatch) error {
	if job == nil {
		return nil
	}
	service, _ := dispatch.Attempt.Action.RecoveryService()
	if job.GetPawnId() != string(service.Pawn()) || job.GetTargetA().GetThingId() != service.Thing() || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *RecoveryServiceBoundary) RecoveryServicePawn(ctx context.Context, dispatch executor.RecoveryServiceDispatch) (executor.Receipt, error) {
	p := dispatch.Attempt
	out := executor.Receipt{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: p.Snapshot, Kind: domain.ReceiptUnknown}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	lease, err := b.leases.Lease(p.Snapshot)
	if err != nil {
		return out, err
	}
	if !boundaryID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation), LeaseId: proto.String(lease)}
	reply, _, err := b.writer.ApplyRecoveryService(ctx, pre, attempt.Owner, attempt.Pawn, attempt.PawnToken, attempt.Thing, attempt.ThingToken, attempt.Method)
	var refused *bridge.NativeFailure
	if errors.As(err, &refused) && refused.Value != nil && refused.Value.GetCode() != c.FailureCode_FAILURE_CODE_ATTEMPT_CONFLICT {
		out.Kind = domain.ReceiptRefused
		return out, nil
	}
	if err != nil {
		return out, err
	}
	receipt := reply.GetReceipt()
	if err = b.checkReceipt(receipt, dispatch); err != nil {
		return out, err
	}
	switch receipt.Outcome.(type) {
	case *r.Receipt_Applied:
		out.Kind = domain.ReceiptAccepted
	case *r.Receipt_Uncertain:
	default:
		return out, executor.ErrEvidence
	}
	return out, ctx.Err()
}

func (b *RecoveryServiceBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.RecoveryServiceDispatch) error {
	p := dispatch.Attempt
	if err := boundaryAdmission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := draftReceiptJob(receipt)
	return recoveryServiceJob(job, dispatch)
}

func (b *RecoveryServiceBoundary) ObserveRecoveryService(ctx context.Context, dispatch executor.RecoveryServiceDispatch, current domain.GenerationSnapshot) (executor.RecoveryServiceEvidence, error) {
	out := executor.RecoveryServiceEvidence{StartedAt: b.clock.Now()}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	reply, _, err := b.native.LookupRecoveryService(ctx, attempt)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Unknown:
		if _, err = boundaryContext(v.Unknown.GetContext(), current); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: domain.Tick(v.Unknown.GetContext().GetTick()), Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, attempt.Attempt) {
			return out, executor.ErrEvidence
		}
		if err = boundaryAdmission(&r.Receipt{Attempt: v.InFlight.Attempt, AdmittedContext: v.InFlight.AdmittedContext, AuthorizingOwner: attempt.Owner}, p, b.session); err != nil {
			return out, err
		}
	case *r.LookupReply_Receipt:
		if err = b.checkReceipt(v.Receipt, dispatch); err != nil {
			return out, err
		}
	default:
		return out, executor.ErrEvidence
	}
	progress, _, err := b.native.ObserveRecoveryServiceProgress(ctx, attempt, nil)
	if err != nil {
		return out, err
	}
	prog := progress.GetProgress()
	if prog == nil {
		return out, executor.ErrHeld
	}
	if !proto.Equal(prog.Attempt, attempt.Attempt) {
		return out, executor.ErrEvidence
	}
	tickCtx, err := boundaryContext(prog.Context, current)
	if err != nil {
		return out, err
	}
	tick := domain.Tick(prog.Context.GetTick())
	out.ObservedAt = b.clock.Now()
	switch outcome := prog.Effect.(type) {
	case *r.Progress_Unknown:
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		return out, ctx.Err()
	case *r.Progress_Pending:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		job := outcome.Pending.GetEvidence().GetJob()
		if err = recoveryServiceJob(job, dispatch); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}
		out.Pawn, out.Thing = mustRecoveryService(dispatch).Pawn(), mustRecoveryService(dispatch).Thing()
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		job := outcome.Completed.GetEvidence().GetJob()
		if err = recoveryServiceJob(job, dispatch); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete, out.Pawn, out.Thing = true, mustRecoveryService(dispatch).Pawn(), mustRecoveryService(dispatch).Thing()
		return out, ctx.Err()
	case *r.Progress_Absent:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectAbsent, Causality: domain.AfterDispatch}
		out.Complete, out.Pawn, out.Thing = true, mustRecoveryService(dispatch).Pawn(), mustRecoveryService(dispatch).Thing()
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		job := outcome.Unsuccessful.GetEvidence().GetJob()
		if err = recoveryServiceJob(job, dispatch); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure, Causality: domain.AfterDispatch}
		out.Complete, out.Pawn, out.Thing = true, mustRecoveryService(dispatch).Pawn(), mustRecoveryService(dispatch).Thing()
		return out, ctx.Err()
	default:
		return out, executor.ErrEvidence
	}
}

func mustRecoveryService(dispatch executor.RecoveryServiceDispatch) domain.RecoveryService {
	service, _ := dispatch.Attempt.Action.RecoveryService()
	return service
}

var _ executor.RecoveryServiceBoundary = (*RecoveryServiceBoundary)(nil)
