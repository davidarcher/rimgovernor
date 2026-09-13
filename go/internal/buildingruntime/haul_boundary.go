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

// HaulNative reuses the generic bridge.ReadPawns (no combat/work/care details
// are needed, unlike tend) and the new bridge.ReadHaulTargets, which is the
// only cell-scoped way to refresh a loose thing's CAS token.
type HaulNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadHaulTargets(context.Context, *c.Identity, domain.Cell) (bridge.HaulRead, bridge.Result, error)
	PreviewPawnOrder(context.Context, *c.Identity, *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error)
	LookupPawnOrderAttempt(context.Context, bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePawnOrderProgress(context.Context, bridge.PawnOrderAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type HaulWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *a.Owner, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}
type HaulCapabilities struct {
	Native HaulNative
	Writer HaulWriter
}
type HaulBoundary struct {
	native  HaulNative
	writer  HaulWriter
	leases  LeaseSource
	clock   executor.Clock
	session string
}

func NewHaulBoundary(native HaulNative, writer HaulWriter, leases LeaseSource, clock executor.Clock, session string) (*HaulBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundaryID(session) {
		return nil, errors.New("invalid haul boundary dependencies")
	}
	return &HaulBoundary{native, writer, leases, clock, session}, nil
}

func haulCommand(pawn, thing, pawnToken, thingToken string) *o.PawnTargetOrder {
	return &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String(pawn), ExpectedSnapshotToken: proto.String(pawnToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(thing), ExpectedSnapshotToken: proto.String(thingToken)}, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_HAUL.Enum(), RequireSafeStorage: proto.Bool(true)}
}

func haulJobDefAllowed(jobDef string) bool {
	return jobDef == "HaulToCell" || jobDef == "HaulToContainer"
}

func (b *HaulBoundary) InspectHaul(ctx context.Context, target executor.Target) (executor.HaulInspection, error) {
	out := executor.HaulInspection{StartedAt: b.clock.Now()}
	haul, ok := target.Action.Haul()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundaryIdentity(target.Snapshot), []string{string(haul.Pawn())})
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
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(haul.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := draftToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	targets, _, err := b.native.ReadHaulTargets(ctx, boundaryIdentity(current), haul.Cell())
	if err != nil {
		return out, err
	}
	if _, err = boundaryContext(targets.Context, current); err != nil {
		return out, err
	}
	if targets.Context.GetTick() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	var thingToken string
	found := false
	for _, candidate := range targets.Targets {
		if candidate.Thing != haul.Thing() {
			continue
		}
		if found || candidate.Definition != haul.Definition() || candidate.Cell != haul.Cell() {
			return out, executor.ErrEvidence
		}
		thingToken, found = candidate.Token, true
	}
	if !found {
		return out, executor.ErrHeld
	}
	preview, _, err := b.native.PreviewPawnOrder(ctx, boundaryIdentity(current), haulCommand(string(haul.Pawn()), haul.Thing(), pawnToken, thingToken))
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
	if evaluated.Context.GetTick() < targets.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(haul.Pawn()) || job.GetTargetA().GetThingId() != haul.Thing() || !haulJobDefAllowed(job.GetJobDef()) || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	facts := policy.HaulFacts{Snapshot: current, PawnTick: domain.Tick(targets.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), ThingSnapshotToken: thingToken, NativeCanTry: draftBool(job.CanTry)}
	facts.Pawn = haulPawnFacts(haul.Pawn(), row, pawnToken)
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func haulPawnFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.HaulPawnFacts {
	facts := policy.HaulPawnFacts{Pawn: pawn, SnapshotToken: token, Dead: draftBool(row.Dead), Downed: draftBool(row.Downed), Drafted: draftBool(row.Drafted), MentalState: draftPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !tendIssue(row.Job.Issues, "player_forced") && !tendIssue(row.Job.Issues, "queued_jobs") && !tendIssue(row.Job.Issues, "def_name") {
		facts.PlayerForced, facts.QueuedJobs = draftBool(row.Job.PlayerForced), draftUint(row.Job.QueuedJobs)
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}

func (b *HaulBoundary) attempt(dispatch executor.HaulDispatch) (bridge.PawnOrderAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	haul, ok := p.Action.Haul()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != haul.Pawn() || admission.Thing != haul.Thing() || admission.Definition != haul.Definition() || admission.Cell != haul.Cell() || admission.Tick > p.Tick || !boundaryID(admission.PawnSnapshotToken) || !boundaryID(admission.ThingSnapshotToken) {
		return bridge.PawnOrderAttempt{}, executor.ErrEvidence
	}
	return bridge.PawnOrderAttempt{Identity: boundaryIdentity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, PawnID: string(haul.Pawn()), TargetID: haul.Thing(), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_HAUL, RequireSafeStorage: true}, nil
}

func haulJob(job *r.JobEffect, dispatch executor.HaulDispatch) error {
	if job == nil {
		return nil
	}
	haul, _ := dispatch.Attempt.Action.Haul()
	if job.GetPawnId() != string(haul.Pawn()) || job.GetTargetA().GetThingId() != haul.Thing() || !haulJobDefAllowed(job.GetJobDef()) || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *HaulBoundary) HaulThing(ctx context.Context, dispatch executor.HaulDispatch) (executor.Receipt, error) {
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
	pre := &a.WritePrecondition{Identity: attempt.Identity, Attempt: attempt.Attempt, ExpectedGeneration: proto.Uint64(attempt.NativeGeneration), LeaseId: proto.String(lease)}
	reply, _, err := b.writer.OrderPawn(ctx, pre, attempt.Owner, haulCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.PawnSnapshotToken, dispatch.Admission.ThingSnapshotToken))
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

func (b *HaulBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.HaulDispatch) error {
	p := dispatch.Attempt
	if err := boundaryAdmission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := draftReceiptJob(receipt)
	return haulJob(job, dispatch)
}

var _ executor.HaulBoundary = (*HaulBoundary)(nil)
