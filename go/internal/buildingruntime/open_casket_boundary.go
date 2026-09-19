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

// OpenCasketNative (#460) reads the opener like Repair does, the casket's
// CAS token through the claim listing (the claim token covers faction and
// whether the casket holds anything, #459) and its contents through the
// shrine census, then previews the OPEN_CASKET pawn order.
type OpenCasketNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadClaimBuildingTarget(context.Context, *c.Identity, string) (bridge.ClaimBuildingTarget, bridge.Result, error)
	ReadAncientShrines(context.Context, *c.Identity) (*n.AncientShrinesReply, bridge.Result, error)
	PreviewPawnOrder(context.Context, *c.Identity, *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error)
	LookupPawnOrderAttempt(context.Context, bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePawnOrderProgress(context.Context, bridge.PawnOrderAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type OpenCasketWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}
type OpenCasketCapabilities struct {
	Native OpenCasketNative
	Writer OpenCasketWriter
}
type OpenCasketBoundary struct {
	native  OpenCasketNative
	writer  OpenCasketWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewOpenCasketBoundary(native OpenCasketNative, writer OpenCasketWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*OpenCasketBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid open casket boundary dependencies")
	}
	return &OpenCasketBoundary{native, writer, leases, clock, session}, nil
}

func openCasketCommand(pawn, casket, pawnToken, casketToken string) *o.PawnTargetOrder {
	return &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String(pawn), ExpectedSnapshotToken: proto.String(pawnToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(casket), ExpectedSnapshotToken: proto.String(casketToken)}, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_OPEN_CASKET.Enum(), RequireSafeStorage: proto.Bool(false)}
}

func (b *OpenCasketBoundary) InspectOpenCasket(ctx context.Context, target executor.Target) (executor.OpenCasketInspection, error) {
	out := executor.OpenCasketInspection{StartedAt: b.clock.Now()}
	open, ok := target.Action.OpenCasket()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(open.Pawn())})
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
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(open.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	casket, _, err := b.native.ReadClaimBuildingTarget(ctx, boundary.Identity(current), open.Casket())
	if err != nil {
		return out, err
	}
	if casket.Context == nil || casket.Thing != open.Casket() {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(casket.Context, current); err != nil {
		return out, err
	}
	// Both target reads may come from the step's fact cache, up to the
	// family's tick tolerance behind the pawn read (#306, #323); the casket
	// token still binds the order to what was listed.
	if bridge.FactColony.Outrun(casket.Context.GetTick(), observed.Context.GetTick()) {
		return out, executor.ErrEvidence
	}
	shrines, _, err := b.native.ReadAncientShrines(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	census := shrines.GetObserved()
	if err = bridge.ValidateAncientShrines(census, boundary.Identity(current)); err != nil {
		return out, err
	}
	if _, err = boundary.Context(census.Context, current); err != nil {
		return out, err
	}
	if bridge.FactColony.Outrun(census.Context.GetTick(), observed.Context.GetTick()) {
		return out, executor.ErrEvidence
	}
	// A casket the census no longer lists as filled has nothing left to
	// open; one it does not list at all is no longer a shrine casket.
	hasContents := domain.Known(false)
	for _, shrine := range census.Shrines {
		for _, listed := range shrine.Caskets {
			if listed.GetEntityId() == open.Casket() {
				if listed.Cell.GetX() != open.Cell().X || listed.Cell.GetZ() != open.Cell().Z {
					return out, executor.ErrEvidence
				}
				hasContents = domain.Known(listed.GetHasContents())
			}
		}
	}
	preview, _, err := b.native.PreviewPawnOrder(ctx, boundary.Identity(current), openCasketCommand(string(open.Pawn()), open.Casket(), pawnToken, casket.Token))
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
	if evaluated.Context.GetTick() < observed.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(open.Pawn()) || job.GetTargetA().GetThingId() != open.Casket() || job.GetJobDef() != "Open" || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	pawn := repairPawnFacts(open.Pawn(), row, pawnToken)
	out.Facts = policy.OpenCasketFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: boundary.FactBool(job.CanTry),
		Pawn:   policy.OpenCasketPawnFacts(pawn),
		Casket: open.Casket(), CasketSnapshotToken: casket.Token, Exists: domain.Known(true), HasContents: hasContents}
	out.ObservedAt = b.clock.Now()
	return out, ctx.Err()
}

func (b *OpenCasketBoundary) attempt(dispatch executor.OpenCasketDispatch) (bridge.PawnOrderAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	open, ok := p.Action.OpenCasket()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != open.Pawn() || admission.Structure != open.Casket() || admission.Cell != open.Cell() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.StructureSnapshotToken) {
		return bridge.PawnOrderAttempt{}, executor.ErrEvidence
	}
	return bridge.PawnOrderAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), PawnID: string(open.Pawn()), TargetID: open.Casket(), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_OPEN_CASKET, RequireSafeStorage: false}, nil
}

// openCasketJob accepts a drafted opener (the melee lock drafts first) but
// no draft claim of its own: the order takes none.
func openCasketJob(job *r.JobEffect, dispatch executor.OpenCasketDispatch) error {
	if job == nil {
		return nil
	}
	open, _ := dispatch.Attempt.Action.OpenCasket()
	if job.GetPawnId() != string(open.Pawn()) || job.GetTargetA().GetThingId() != open.Casket() || job.GetJobDef() != "Open" || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *OpenCasketBoundary) OpenCasket(ctx context.Context, dispatch executor.OpenCasketDispatch) (executor.Receipt, error) {
	return boundary.DispatchPawnOrder(ctx, b.leases, b.writer, dispatch.Attempt,
		func() (bridge.PawnOrderAttempt, error) { return b.attempt(dispatch) },
		func(attempt bridge.PawnOrderAttempt) *o.PawnTargetOrder {
			return openCasketCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.PawnSnapshotToken, dispatch.Admission.StructureSnapshotToken)
		},
		func(receipt *r.Receipt) error { return b.checkReceipt(receipt, dispatch) },
	)
}

func (b *OpenCasketBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.OpenCasketDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return openCasketJob(boundary.ReceiptJob(receipt), dispatch)
}

var _ executor.OpenCasketBoundary = (*OpenCasketBoundary)(nil)
