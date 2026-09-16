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

// CleanNative reuses the generic bridge.ReadPawns (no combat/work/care
// details are needed, unlike tend) and the new bridge.ReadFilthTarget, which
// is the only way to refresh a filth entity's CAS token (filth has no
// exact-ID lookup RPC like ListBuildings).
type CleanNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadFilthTarget(context.Context, *c.Identity, string, domain.Cell) (bridge.FilthTarget, bridge.Result, error)
	PreviewPawnOrder(context.Context, *c.Identity, *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error)
	LookupPawnOrderAttempt(context.Context, bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePawnOrderProgress(context.Context, bridge.PawnOrderAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type CleanWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}
type CleanCapabilities struct {
	Native CleanNative
	Writer CleanWriter
}
type CleanBoundary struct {
	native  CleanNative
	writer  CleanWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewCleanBoundary(native CleanNative, writer CleanWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*CleanBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid clean boundary dependencies")
	}
	return &CleanBoundary{native, writer, leases, clock, session}, nil
}

func cleanCommand(pawn, filth, pawnToken, filthToken string) *o.PawnTargetOrder {
	return &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String(pawn), ExpectedSnapshotToken: proto.String(pawnToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(filth), ExpectedSnapshotToken: proto.String(filthToken)}, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_CLEAN.Enum(), RequireSafeStorage: proto.Bool(false)}
}

func cleanPawnFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.CleanPawnFacts {
	facts := policy.CleanPawnFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "def_name") {
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}

func (b *CleanBoundary) InspectClean(ctx context.Context, target executor.Target) (executor.CleanInspection, error) {
	out := executor.CleanInspection{StartedAt: b.clock.Now()}
	clean, ok := target.Action.Clean()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(clean.Pawn())})
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
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(clean.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	filth, _, err := b.native.ReadFilthTarget(ctx, boundary.Identity(current), clean.Filth(), clean.Cell())
	if err != nil {
		return out, err
	}
	if filth.Context == nil || filth.Filth != clean.Filth() {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(filth.Context, current); err != nil {
		return out, err
	}
	if filth.Context.GetTick() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	filthToken := filth.Token
	preview, _, err := b.native.PreviewPawnOrder(ctx, boundary.Identity(current), cleanCommand(string(clean.Pawn()), clean.Filth(), pawnToken, filthToken))
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
	if evaluated.Context.GetTick() < observed.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(clean.Pawn()) || job.GetTargetA().GetThingId() != clean.Filth() || job.GetJobDef() != "Clean" || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	facts := policy.CleanFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: boundary.FactBool(job.CanTry)}
	facts.Pawn = cleanPawnFacts(clean.Pawn(), row, pawnToken)
	facts.Filth = policy.CleanFilthFacts{Filth: clean.Filth(), SnapshotToken: filthToken, Exists: domain.Known(true)}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *CleanBoundary) attempt(dispatch executor.CleanDispatch) (bridge.PawnOrderAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	clean, ok := p.Action.Clean()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != clean.Pawn() || admission.Filth != clean.Filth() || admission.Cell != clean.Cell() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.FilthSnapshotToken) {
		return bridge.PawnOrderAttempt{}, executor.ErrEvidence
	}
	return bridge.PawnOrderAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), PawnID: string(clean.Pawn()), TargetID: clean.Filth(), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_CLEAN, RequireSafeStorage: false}, nil
}

func cleanJob(job *r.JobEffect, dispatch executor.CleanDispatch) error {
	if job == nil {
		return nil
	}
	clean, _ := dispatch.Attempt.Action.Clean()
	if job.GetPawnId() != string(clean.Pawn()) || job.GetTargetA().GetThingId() != clean.Filth() || job.GetJobDef() != "Clean" || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *CleanBoundary) CleanFilth(ctx context.Context, dispatch executor.CleanDispatch) (executor.Receipt, error) {
	return boundary.DispatchPawnOrder(ctx, b.leases, b.writer, dispatch.Attempt,
		func() (bridge.PawnOrderAttempt, error) { return b.attempt(dispatch) },
		func(attempt bridge.PawnOrderAttempt) *o.PawnTargetOrder {
			return cleanCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.PawnSnapshotToken, dispatch.Admission.FilthSnapshotToken)
		},
		func(receipt *r.Receipt) error { return b.checkReceipt(receipt, dispatch) },
	)
}

func (b *CleanBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.CleanDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := boundary.ReceiptJob(receipt)
	return cleanJob(job, dispatch)
}

var _ executor.CleanBoundary = (*CleanBoundary)(nil)
