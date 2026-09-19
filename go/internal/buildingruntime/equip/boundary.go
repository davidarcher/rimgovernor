package equip

import (
	"context"
	"errors"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"

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

// EquipNative reuses the generic bridge.ReadPawns and the new
// bridge.ReadEquipWeapons, which is the only way to refresh a loose weapon's
// CAS token.
type EquipNative interface {
	ReadPawns(context.Context, *c.Identity, []string) (*n.ListPawnsReply, bridge.Result, error)
	ReadEquipWeapons(context.Context, *c.Identity, domain.Cell, domain.Cell) (bridge.EquipRead, bridge.Result, error)
	PreviewPawnOrder(context.Context, *c.Identity, *o.PawnTargetOrder) (*o.PreviewReply, bridge.Result, error)
	LookupPawnOrderAttempt(context.Context, bridge.PawnOrderAttempt) (*r.LookupReply, bridge.Result, error)
	ObservePawnOrderProgress(context.Context, bridge.PawnOrderAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type EquipWriter interface {
	OrderPawn(context.Context, *a.WritePrecondition, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}
type EquipCapabilities struct {
	Native EquipNative
	Writer EquipWriter
}
type EquipBoundary struct {
	native  EquipNative
	writer  EquipWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewEquipBoundary(native EquipNative, writer EquipWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*EquipBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid equip boundary dependencies")
	}
	return &EquipBoundary{native, writer, leases, clock, session}, nil
}

func equipCommand(pawn, thing, pawnToken, thingToken string) *o.PawnTargetOrder {
	return &o.PawnTargetOrder{Pawn: &o.EntityPrecondition{EntityId: proto.String(pawn), ExpectedSnapshotToken: proto.String(pawnToken)}, Target: &o.EntityPrecondition{EntityId: proto.String(thing), ExpectedSnapshotToken: proto.String(thingToken)}, Kind: o.PawnOrderKind_PAWN_ORDER_KIND_EQUIP.Enum(), RequireSafeStorage: proto.Bool(false)}
}

func equipJobDefAllowed(jobDef string) bool {
	return jobDef == "Equip"
}

func (b *EquipBoundary) InspectEquip(ctx context.Context, target executor.Target) (executor.EquipInspection, error) {
	out := executor.EquipInspection{StartedAt: b.clock.Now()}
	equip, ok := target.Action.Equip()
	if !ok {
		return out, executor.ErrEvidence
	}
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(equip.Pawn())})
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
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(equip.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	targets, _, err := b.native.ReadEquipWeapons(ctx, boundary.Identity(current), equip.Cell(), equip.Cell())
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(targets.Context, current); err != nil {
		return out, err
	}
	// The target read may come from the step's fact cache under a running
	// window, up to its family's tick tolerance behind the pawn read, the
	// step's first native read (#306, #323); its snapshot token still binds
	// the order to what it listed, so native refuses a stale one at dispatch.
	// Only a read the pawn read has outrun is wrong evidence.
	if targets.Context.GetTick()+bridge.FactColony.TickTolerance() < observed.Context.GetTick() {
		return out, executor.ErrEvidence
	}
	var thingToken string
	found := false
	for _, candidate := range targets.Targets {
		if candidate.Thing != equip.Thing() {
			continue
		}
		if found || candidate.Definition != equip.Definition() || candidate.Cell != equip.Cell() {
			return out, executor.ErrEvidence
		}
		thingToken, found = candidate.Token, true
	}
	if !found {
		return out, executor.ErrHeld
	}
	preview, _, err := b.native.PreviewPawnOrder(ctx, boundary.Identity(current), equipCommand(string(equip.Pawn()), equip.Thing(), pawnToken, thingToken))
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
	if evaluated.Context.GetTick() < targets.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(equip.Pawn()) || job.GetTargetA().GetThingId() != equip.Thing() || !equipJobDefAllowed(job.GetJobDef()) || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	facts := policy.EquipFacts{Snapshot: current, PawnTick: domain.Tick(targets.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), ThingSnapshotToken: thingToken, NativeCanTry: boundary.FactBool(job.CanTry)}
	facts.Pawn = equipPawnFacts(equip.Pawn(), row, pawnToken)
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func equipPawnFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.EquipPawnFacts {
	facts := policy.EquipPawnFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "def_name") {
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}

func (b *EquipBoundary) attempt(dispatch executor.EquipDispatch) (bridge.PawnOrderAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	equip, ok := p.Action.Equip()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != equip.Pawn() || admission.Thing != equip.Thing() || admission.Definition != equip.Definition() || admission.Cell != equip.Cell() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.ThingSnapshotToken) {
		return bridge.PawnOrderAttempt{}, executor.ErrEvidence
	}
	return bridge.PawnOrderAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), PawnID: string(equip.Pawn()), TargetID: equip.Thing(), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_EQUIP, RequireSafeStorage: false}, nil
}

func equipJob(job *r.JobEffect, dispatch executor.EquipDispatch) error {
	if job == nil {
		return nil
	}
	equip, _ := dispatch.Attempt.Action.Equip()
	if job.GetPawnId() != string(equip.Pawn()) || job.GetTargetA().GetThingId() != equip.Thing() || !equipJobDefAllowed(job.GetJobDef()) || job.JobId == nil || job.GetJobId() < 0 {
		return executor.ErrEvidence
	}
	if job.Drafted != nil && job.GetDrafted() || job.DraftClaimId != nil || job.DraftOwner != nil {
		return executor.ErrEvidence
	}
	return nil
}

func (b *EquipBoundary) EquipPawn(ctx context.Context, dispatch executor.EquipDispatch) (executor.Receipt, error) {
	return boundary.DispatchPawnOrder(ctx, b.leases, b.writer, dispatch.Attempt,
		func() (bridge.PawnOrderAttempt, error) { return b.attempt(dispatch) },
		func(attempt bridge.PawnOrderAttempt) *o.PawnTargetOrder {
			return equipCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.PawnSnapshotToken, dispatch.Admission.ThingSnapshotToken)
		},
		func(receipt *r.Receipt) error { return b.checkReceipt(receipt, dispatch) },
	)
}

func (b *EquipBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.EquipDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := boundary.ReceiptJob(receipt)
	return equipJob(job, dispatch)
}

var _ executor.EquipBoundary = (*EquipBoundary)(nil)
