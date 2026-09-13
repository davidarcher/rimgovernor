package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
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

// GearReplaceNative reuses the generic bridge.ReadPawns for pawn eligibility
// facts (same shape as EquipNative's) plus the new bridge.ReadGearReplacement
// for the pawn/thing/loadout CAS tokens the native ImproveGear operation
// requires.
type GearReplaceNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadGearReplacement(context.Context, *c.Identity, string, string) (bridge.GearReplaceRead, bridge.Result, error)
	PreviewGearReplace(context.Context, *c.Identity, string, string, string, string, string) (*o.PreviewReply, bridge.Result, error)
	LookupGearReplace(context.Context, bridge.GearReplaceAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveGearReplaceProgress(context.Context, bridge.GearReplaceAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type GearReplaceWriter interface {
	ApplyGearReplace(context.Context, *a.WritePrecondition, *a.Owner, string, string, string, string, string) (*o.ExecuteReply, bridge.Result, error)
}
type GearReplaceCapabilities struct {
	Native GearReplaceNative
	Writer GearReplaceWriter
}
type GearReplaceBoundary struct {
	native  GearReplaceNative
	writer  GearReplaceWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewGearReplaceBoundary(native GearReplaceNative, writer GearReplaceWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*GearReplaceBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid gear replace boundary dependencies")
	}
	return &GearReplaceBoundary{native, writer, leases, clock, session}, nil
}

func (b *GearReplaceBoundary) InspectGearReplace(ctx context.Context, target executor.Target) (executor.GearReplaceInspection, error) {
	out := executor.GearReplaceInspection{StartedAt: b.clock.Now()}
	replace, ok := target.Action.GearReplace()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(replace.Pawn())})
	if err != nil {
		return out, err
	}
	observed := reply.GetObserved()
	if observed == nil {
		return out, executor.ErrHeld
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	counts := observed.Completeness
	if counts == nil || counts.Page == nil || !counts.Page.GetComplete() || counts.Page.GetNextCursor() != "" || counts.Matched == nil || counts.Returned == nil || counts.Unreadable == nil || counts.GetUnreadable() != 0 || counts.GetMatched() != 1 || counts.GetReturned() != 1 || len(observed.Pawns) != 1 {
		return out, executor.ErrHeld
	}
	row := observed.Pawns[0]
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(replace.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	gear, _, err := b.native.ReadGearReplacement(ctx, boundary.Identity(current), string(replace.Pawn()), replace.Thing())
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(gear.Context, current); err != nil {
		return out, err
	}
	if gear.Context.GetTick() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	if gear.Definition != replace.Definition() {
		return out, executor.ErrEvidence
	}
	if gear.PawnToken != pawnToken {
		return out, executor.ErrHeld
	}
	preview, _, err := b.native.PreviewGearReplace(ctx, boundary.Identity(current), string(replace.Pawn()), pawnToken, replace.Thing(), gear.ThingToken, gear.LoadoutToken)
	if err != nil {
		return out, err
	}
	evaluated := preview.GetEvaluated()
	if evaluated == nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(evaluated.Context, current); err != nil {
		return out, err
	}
	job := evaluated.GetProjected().GetJob()
	if evaluated.Context.GetTick() < gear.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(replace.Pawn()) || job.GetTargetA().GetThingId() != replace.Thing() || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	facts := policy.GearReplaceFacts{Snapshot: current, PawnTick: domain.Tick(gear.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), ThingSnapshotToken: gear.ThingToken, LoadoutToken: gear.LoadoutToken, NativeCanTry: boundary.FactBool(job.CanTry)}
	facts.Pawn = gearReplacePawnFacts(replace.Pawn(), row, pawnToken)
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func gearReplacePawnFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.GearReplacePawnFacts {
	facts := policy.GearReplacePawnFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "player_forced") && !boundary.IssueField(row.Job.Issues, "queued_jobs") && !boundary.IssueField(row.Job.Issues, "def_name") {
		facts.PlayerForced, facts.QueuedJobs = boundary.FactBool(row.Job.PlayerForced), boundary.FactUint(row.Job.QueuedJobs)
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}

func (b *GearReplaceBoundary) attempt(dispatch executor.GearReplaceDispatch) (bridge.GearReplaceAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	replace, ok := p.Action.GearReplace()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != replace.Pawn() || admission.Thing != replace.Thing() || admission.Definition != replace.Definition() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.ThingSnapshotToken) || !boundary.ValidID(admission.LoadoutToken) {
		return bridge.GearReplaceAttempt{}, executor.ErrEvidence
	}
	return bridge.GearReplaceAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, Generation: uint64(p.Snapshot.Native), Pawn: string(replace.Pawn()), Thing: replace.Thing(), PawnToken: admission.PawnSnapshotToken, ThingToken: admission.ThingSnapshotToken, LoadoutToken: admission.LoadoutToken}, nil
}

func gearReplaceJob(job *r.JobEffect, dispatch executor.GearReplaceDispatch) error {
	if job == nil {
		return nil
	}
	replace, _ := dispatch.Attempt.Action.GearReplace()
	if job.GetPawnId() != string(replace.Pawn()) || job.GetTargetA().GetThingId() != replace.Thing() || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *GearReplaceBoundary) GearReplacePawn(ctx context.Context, dispatch executor.GearReplaceDispatch) (executor.Receipt, error) {
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
	if !boundary.ValidID(lease) {
		return out, executor.ErrAuthority
	}
	if err = ctx.Err(); err != nil {
		return out, err
	}
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.Generation), LeaseId: proto.String(lease)}
	reply, _, err := b.writer.ApplyGearReplace(ctx, pre, attempt.Owner, attempt.Pawn, attempt.PawnToken, attempt.Thing, attempt.ThingToken, attempt.LoadoutToken)
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

func (b *GearReplaceBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.GearReplaceDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := boundary.ReceiptJob(receipt)
	return gearReplaceJob(job, dispatch)
}

func (b *GearReplaceBoundary) ObserveGearReplace(ctx context.Context, dispatch executor.GearReplaceDispatch, current domain.GenerationSnapshot) (executor.GearReplaceEvidence, error) {
	out := executor.GearReplaceEvidence{StartedAt: b.clock.Now()}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	reply, _, err := b.native.LookupGearReplace(ctx, attempt)
	if err != nil {
		return out, err
	}
	p := dispatch.Attempt
	switch v := reply.Outcome.(type) {
	case *r.LookupReply_Unknown:
		if _, err = boundary.Context(v.Unknown.GetContext(), current); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Tick: domain.Tick(v.Unknown.GetContext().GetTick()), Effect: domain.EffectUnknown, Causality: domain.AfterDispatch}
		out.ObservedAt = b.clock.Now()
		return out, ctx.Err()
	case *r.LookupReply_InFlight:
		if v.InFlight == nil || !proto.Equal(v.InFlight.Attempt, attempt.Attempt) {
			return out, executor.ErrEvidence
		}
		if err = boundary.Admission(&r.Receipt{Attempt: v.InFlight.Attempt, AdmittedContext: v.InFlight.AdmittedContext, AuthorizingOwner: attempt.Owner}, p, b.session); err != nil {
			return out, err
		}
	case *r.LookupReply_Receipt:
		if err = b.checkReceipt(v.Receipt, dispatch); err != nil {
			return out, err
		}
	default:
		return out, executor.ErrEvidence
	}
	progress, _, err := b.native.ObserveGearReplaceProgress(ctx, attempt, nil)
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
	tickCtx, err := boundary.Context(prog.Context, current)
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
		if err = gearReplaceJob(job, dispatch); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}
		out.Pawn, out.Thing = mustGearReplace(dispatch).Pawn(), mustGearReplace(dispatch).Thing()
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		job := outcome.Completed.GetEvidence().GetJob()
		if err = gearReplaceJob(job, dispatch); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete, out.Pawn, out.Thing = true, mustGearReplace(dispatch).Pawn(), mustGearReplace(dispatch).Thing()
		return out, ctx.Err()
	case *r.Progress_Absent:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectAbsent, Causality: domain.AfterDispatch}
		out.Complete, out.Pawn, out.Thing = true, mustGearReplace(dispatch).Pawn(), mustGearReplace(dispatch).Thing()
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		job := outcome.Unsuccessful.GetEvidence().GetJob()
		if err = gearReplaceJob(job, dispatch); err != nil {
			return out, err
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure, Causality: domain.AfterDispatch}
		out.Complete, out.Pawn, out.Thing = true, mustGearReplace(dispatch).Pawn(), mustGearReplace(dispatch).Thing()
		return out, ctx.Err()
	default:
		return out, executor.ErrEvidence
	}
}

func mustGearReplace(dispatch executor.GearReplaceDispatch) domain.GearReplace {
	replace, _ := dispatch.Attempt.Action.GearReplace()
	return replace
}

var _ executor.GearReplaceBoundary = (*GearReplaceBoundary)(nil)
