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

// RepairNative reuses the generic bridge.ReadPawns (no combat/work/care
// details are needed, unlike tend) and the new bridge.ReadRepairTarget, which
// is the only exact-ID way to refresh a structure's CAS token.
type RepairNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadRepairTarget(context.Context, *c.Identity, string) (bridge.RepairTarget, bridge.Result, error)
	PreviewPawnOrder(context.Context, *c.Identity, *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error)
	LookupPawnOrderAttempt(context.Context, bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePawnOrderProgress(context.Context, bridge.PawnOrderAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type RepairWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}
type RepairCapabilities struct {
	Native RepairNative
	Writer RepairWriter
}
type RepairBoundary struct {
	native  RepairNative
	writer  RepairWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewRepairBoundary(native RepairNative, writer RepairWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*RepairBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid repair boundary dependencies")
	}
	return &RepairBoundary{native, writer, leases, clock, session}, nil
}

func repairCommand(pawn, structure, pawnToken, structureToken string) *o.PawnTargetOrder {
	return &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String(pawn), ExpectedSnapshotToken: proto.String(pawnToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(structure), ExpectedSnapshotToken: proto.String(structureToken)}, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_REPAIR.Enum(), RequireSafeStorage: proto.Bool(false)}
}

func repairPawnFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.RepairPawnFacts {
	facts := policy.RepairPawnFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "def_name") {
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}

func (b *RepairBoundary) InspectRepair(ctx context.Context, target executor.Target) (executor.RepairInspection, error) {
	out := executor.RepairInspection{StartedAt: b.clock.Now()}
	repair, ok := target.Action.Repair()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(repair.Pawn())})
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
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(repair.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	structure, _, err := b.native.ReadRepairTarget(ctx, boundary.Identity(current), repair.Structure())
	if err != nil {
		return out, err
	}
	if structure.Context == nil || structure.Structure != repair.Structure() {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(structure.Context, current); err != nil {
		return out, err
	}
	if structure.Context.GetTick() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	if structure.MaxHitPoints < structure.HitPoints || structure.MaxHitPoints <= 0 {
		return out, executor.ErrEvidence
	}
	structureToken := structure.Token
	damaged := domain.Known(structure.HitPoints < structure.MaxHitPoints)
	preview, _, err := b.native.PreviewPawnOrder(ctx, boundary.Identity(current), repairCommand(string(repair.Pawn()), repair.Structure(), pawnToken, structureToken))
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
	if evaluated.Context.GetTick() < observed.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(repair.Pawn()) || job.GetTargetA().GetThingId() != repair.Structure() || job.GetJobDef() != "Repair" || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	facts := policy.RepairFacts{Snapshot: current, PawnTick: domain.Tick(observed.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), NativeCanTry: boundary.FactBool(job.CanTry)}
	facts.Pawn = repairPawnFacts(repair.Pawn(), row, pawnToken)
	facts.Structure = policy.RepairStructureFacts{Structure: repair.Structure(), SnapshotToken: structureToken, Exists: domain.Known(true), Damaged: damaged}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *RepairBoundary) attempt(dispatch executor.RepairDispatch) (bridge.PawnOrderAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	repair, ok := p.Action.Repair()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != repair.Pawn() || admission.Structure != repair.Structure() || admission.Cell != repair.Cell() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.StructureSnapshotToken) {
		return bridge.PawnOrderAttempt{}, executor.ErrEvidence
	}
	return bridge.PawnOrderAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), PawnID: string(repair.Pawn()), TargetID: repair.Structure(), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_REPAIR, RequireSafeStorage: false}, nil
}

func repairJob(job *r.JobEffect, dispatch executor.RepairDispatch) error {
	if job == nil {
		return nil
	}
	repair, _ := dispatch.Attempt.Action.Repair()
	if job.GetPawnId() != string(repair.Pawn()) || job.GetTargetA().GetThingId() != repair.Structure() || job.GetJobDef() != "Repair" || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *RepairBoundary) RepairStructure(ctx context.Context, dispatch executor.RepairDispatch) (executor.Receipt, error) {
	return boundary.DispatchPawnOrder(ctx, b.leases, b.writer, dispatch.Attempt,
		func() (bridge.PawnOrderAttempt, error) { return b.attempt(dispatch) },
		func(attempt bridge.PawnOrderAttempt) *o.PawnTargetOrder {
			return repairCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.PawnSnapshotToken, dispatch.Admission.StructureSnapshotToken)
		},
		func(receipt *r.Receipt) error { return b.checkReceipt(receipt, dispatch) },
	)
}

func (b *RepairBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.RepairDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := boundary.ReceiptJob(receipt)
	return repairJob(job, dispatch)
}

var _ executor.RepairBoundary = (*RepairBoundary)(nil)
