// Package haul adapts typed native evidence for the haul action family to
// the deterministic executor.
package haul

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
	OrderPawn(context.Context, *a.WritePrecondition, *o.PawnTargetOrder) (*o.ExecuteReply, bridge.Result, error)
}
type HaulCapabilities struct {
	Native HaulNative
	Writer HaulWriter
}
type HaulBoundary struct {
	native  HaulNative
	writer  HaulWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewHaulBoundary(native HaulNative, writer HaulWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*HaulBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
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
	reply, _, err := b.native.ReadPawns(ctx, boundary.Identity(target.Snapshot), []string{string(haul.Pawn())})
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
	if row == nil || row.Pawn == nil || row.Pawn.GetId() != string(haul.Pawn()) {
		return out, executor.ErrEvidence
	}
	pawnToken, err := boundary.PawnToken(row, observed.Context)
	if err != nil {
		return out, err
	}
	targets, _, err := b.native.ReadHaulTargets(ctx, boundary.Identity(current), haul.Cell())
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(targets.Context, current); err != nil {
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
		// The thing left the selected cell (ordinary hauling, consumption or
		// destruction). Report a known absence so the executor records a
		// visible thing_absent hold rather than retrying silently.
		facts := policy.HaulFacts{Snapshot: current, PawnTick: domain.Tick(targets.Context.GetTick()), PreviewTick: domain.Tick(targets.Context.GetTick()), ThingPresent: domain.Known(false), NativeCanTry: domain.Known(false)}
		facts.Pawn = haulPawnFacts(haul.Pawn(), row, pawnToken)
		out.Facts, out.ObservedAt = facts, b.clock.Now()
		return out, ctx.Err()
	}
	preview, _, err := b.native.PreviewPawnOrder(ctx, boundary.Identity(current), haulCommand(string(haul.Pawn()), haul.Thing(), pawnToken, thingToken))
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
	if evaluated.Context.GetTick() < targets.Context.GetTick() || evaluated.Accepted == nil || job == nil || job.GetPawnId() != string(haul.Pawn()) || job.GetTargetA().GetThingId() != haul.Thing() || !haulJobDefAllowed(job.GetJobDef()) || job.CanTry == nil || job.GetCanTry() != evaluated.GetAccepted() {
		return out, executor.ErrEvidence
	}
	facts := policy.HaulFacts{Snapshot: current, PawnTick: domain.Tick(targets.Context.GetTick()), PreviewTick: domain.Tick(evaluated.Context.GetTick()), ThingSnapshotToken: thingToken, ThingPresent: domain.Known(true), NativeCanTry: boundary.FactBool(job.CanTry)}
	facts.Pawn = haulPawnFacts(haul.Pawn(), row, pawnToken)
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func haulPawnFacts(pawn domain.PawnID, row *n.PawnState, token string) policy.HaulPawnFacts {
	facts := policy.HaulPawnFacts{Pawn: pawn, SnapshotToken: token, Dead: boundary.FactBool(row.Dead), Downed: boundary.FactBool(row.Downed), Drafted: boundary.FactBool(row.Drafted), MentalState: boundary.FactPresence(row.MentalState, row.Issues, "mental_state")}
	if row.Job != nil && !boundary.IssueField(row.Job.Issues, "def_name") {
		if row.Job.DefName != nil {
			facts.ExistingJobDef = domain.Known(row.Job.GetDefName())
		}
	}
	return facts
}

func (b *HaulBoundary) attempt(dispatch executor.HaulDispatch) (bridge.PawnOrderAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	haul, ok := p.Action.Haul()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Pawn != haul.Pawn() || admission.Thing != haul.Thing() || admission.Definition != haul.Definition() || admission.Cell != haul.Cell() || admission.Tick > p.Tick || !boundary.ValidID(admission.PawnSnapshotToken) || !boundary.ValidID(admission.ThingSnapshotToken) {
		return bridge.PawnOrderAttempt{}, executor.ErrEvidence
	}
	return bridge.PawnOrderAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, NativeGeneration: uint64(p.Snapshot.Native), PawnID: string(haul.Pawn()), TargetID: haul.Thing(), Kind: o.PawnOrderKind_PAWN_ORDER_KIND_HAUL, RequireSafeStorage: true}, nil
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
	return boundary.DispatchPawnOrder(ctx, b.leases, b.writer, dispatch.Attempt,
		func() (bridge.PawnOrderAttempt, error) { return b.attempt(dispatch) },
		func(attempt bridge.PawnOrderAttempt) *o.PawnTargetOrder {
			return haulCommand(attempt.PawnID, attempt.TargetID, dispatch.Admission.PawnSnapshotToken, dispatch.Admission.ThingSnapshotToken)
		},
		func(receipt *r.Receipt) error { return b.checkReceipt(receipt, dispatch) },
	)
}

func (b *HaulBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.HaulDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	job := boundary.ReceiptJob(receipt)
	return haulJob(job, dispatch)
}

var _ executor.HaulBoundary = (*HaulBoundary)(nil)
