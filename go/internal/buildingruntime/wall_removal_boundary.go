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
	o "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// WallRemovalNative reuses bridge.ReadWallUpgradeSites for a fresh
// occupant/geometry read: unlike BedAssign or Repair, a wall-upgrade site has
// no exact per-entity CAS token, so refreshing before dispatch means
// re-listing candidate sites and matching the exact one this step targets.
type WallRemovalNative interface {
	ReadWallUpgradeSites(context.Context, *c.Identity, string) (bridge.WallUpgradeSites, bridge.Result, error)
	LookupWallRemoval(context.Context, bridge.WallRemovalAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveWallRemovalProgress(context.Context, bridge.WallRemovalAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type WallRemovalWriter interface {
	ApplyWallRemoval(context.Context, *a.WritePrecondition, *a.Owner, string) (*o.ExecuteReply, bridge.Result, error)
}
type WallRemovalCapabilities struct {
	Native WallRemovalNative
	Writer WallRemovalWriter
}
type WallRemovalBoundary struct {
	native  WallRemovalNative
	writer  WallRemovalWriter
	leases  boundary.LeaseSource
	clock   executor.Clock
	session string
}

func NewWallRemovalBoundary(native WallRemovalNative, writer WallRemovalWriter, leases boundary.LeaseSource, clock executor.Clock, session string) (*WallRemovalBoundary, error) {
	if native == nil || writer == nil || leases == nil || clock == nil || !boundary.ValidID(session) {
		return nil, errors.New("invalid wall removal boundary dependencies")
	}
	return &WallRemovalBoundary{native, writer, leases, clock, session}, nil
}

// site finds the unique row matching this exact demolition/backup removal.
// The original demolition is looked up by the wall's own identity; a backup
// removal carries no such identity (domain.WallRemoval clears it once a
// same-plan backup takes over), so it is matched by exact geometry instead.
func site(removal domain.WallRemoval, rows []bridge.WallUpgradeSite) (bridge.WallUpgradeSite, bool, error) {
	var found *bridge.WallUpgradeSite
	for i, row := range rows {
		match := row.X == removal.X() && row.Z == removal.Z() && row.NX == removal.NX() && row.NZ == removal.NZ()
		if removal.BackupOf() == "" {
			match = match && row.TargetID == removal.Original()
		}
		if !match {
			continue
		}
		if found != nil {
			return bridge.WallUpgradeSite{}, false, executor.ErrEvidence
		}
		found = &rows[i]
	}
	if found == nil {
		return bridge.WallUpgradeSite{}, false, nil
	}
	return *found, true, nil
}

func (b *WallRemovalBoundary) InspectWallRemoval(ctx context.Context, target executor.Target) (executor.WallRemovalInspection, error) {
	out := executor.WallRemovalInspection{StartedAt: b.clock.Now()}
	removal, ok := target.Action.WallRemoval()
	if !ok {
		return out, executor.ErrEvidence
	}
	queryTarget := removal.Original()
	observed, _, err := b.native.ReadWallUpgradeSites(ctx, boundary.Identity(target.Snapshot), queryTarget)
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(observed.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	row, found, err := site(removal, observed.Sites)
	if err != nil {
		return out, err
	}
	facts := policy.WallRemovalFacts{Snapshot: current, ObservationTick: domain.Tick(observed.Context.GetTick())}
	if found {
		facts.TargetIdentity = domain.Known(row.TargetID)
		facts.SiteEligible = domain.Known(row.Eligible())
	} else {
		facts.TargetIdentity = domain.Unknown[string]()
		facts.SiteEligible = domain.Unknown[bool]()
	}
	out.Facts, out.ObservedAt = facts, b.clock.Now()
	return out, ctx.Err()
}

func (b *WallRemovalBoundary) attempt(dispatch executor.WallRemovalDispatch) (bridge.WallRemovalAttempt, error) {
	p, admission := dispatch.Attempt, dispatch.Admission
	removal, ok := p.Action.WallRemoval()
	if !ok || p.Attempt == 0 || p.Tick < 0 || admission.Snapshot != p.Snapshot || admission.Original != removal.Original() || admission.BackupOf != removal.BackupOf() || admission.Tick > p.Tick || admission.TargetIdentity == "" {
		return bridge.WallRemovalAttempt{}, executor.ErrEvidence
	}
	return bridge.WallRemovalAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: &c.AttemptKey{ControllerSessionId: proto.String(b.session), ActionId: proto.String(string(p.Action.ID())), AttemptId: proto.Uint64(uint64(p.Attempt))}, Owner: &a.Owner{ControllerSessionId: proto.String(b.session), PlayerDirection: proto.Uint64(uint64(p.Snapshot.Direction))}, Generation: uint64(p.Snapshot.Native), Target: admission.TargetIdentity}, nil
}

func (b *WallRemovalBoundary) ExecuteWallRemoval(ctx context.Context, dispatch executor.WallRemovalDispatch) (executor.Receipt, error) {
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
	reply, _, err := b.writer.ApplyWallRemoval(ctx, pre, attempt.Owner, attempt.Target)
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

func (b *WallRemovalBoundary) checkReceipt(receipt *r.Receipt, dispatch executor.WallRemovalDispatch) error {
	p := dispatch.Attempt
	if err := boundary.Admission(receipt, p, b.session); err != nil {
		return err
	}
	if receipt.AdmittedContext.GetTick() < int64(dispatch.Admission.Tick) {
		return executor.ErrEvidence
	}
	return nil
}

func (b *WallRemovalBoundary) ObserveWallRemoval(ctx context.Context, dispatch executor.WallRemovalDispatch, current domain.GenerationSnapshot) (executor.WallRemovalEvidence, error) {
	out := executor.WallRemovalEvidence{StartedAt: b.clock.Now()}
	attempt, err := b.attempt(dispatch)
	if err != nil {
		return out, err
	}
	reply, _, err := b.native.LookupWallRemoval(ctx, attempt)
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
	progress, _, err := b.native.ObserveWallRemovalProgress(ctx, attempt, nil)
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
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectPending, Causality: domain.AfterDispatch}
		out.Target = attempt.Target
		return out, ctx.Err()
	case *r.Progress_Completed:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectCompleted, Causality: domain.AfterDispatch}
		out.Complete, out.Target = true, attempt.Target
		return out, ctx.Err()
	case *r.Progress_Absent:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectAbsent, Causality: domain.AfterDispatch}
		out.Complete, out.Target = true, attempt.Target
		return out, ctx.Err()
	case *r.Progress_Unsuccessful:
		if !prog.GetCompleteInspection() {
			return out, executor.ErrHeld
		}
		out.Observation = domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: tickCtx, Tick: tick, Effect: domain.EffectUnsuccessful, UnsuccessfulReason: domain.NativeFailure, Causality: domain.AfterDispatch}
		out.Complete, out.Target = true, attempt.Target
		return out, ctx.Err()
	default:
		_ = outcome
		return out, executor.ErrEvidence
	}
}

var _ executor.WallRemovalBoundary = (*WallRemovalBoundary)(nil)
