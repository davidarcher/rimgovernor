package buildingruntime

import (
	"context"
	"sort"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	rp "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

// ProductionPolicyNative and ProductionPolicyWriter narrow *bridge.Client and
// *bridge.ProductionPolicyWriter to what productionPolicyBoundary consumes,
// the same split ResearchSelect/GearReplace use.
type ProductionPolicyNative interface {
	ReadProductionPolicy(context.Context, *c.Identity) (bridge.ProductionPolicyRead, bridge.Result, error)
	PreviewProductionPolicy(context.Context, *c.Identity, bridge.ProductionPolicyTarget) (*op.PreviewReply, bridge.Result, error)
	LookupProductionPolicy(context.Context, bridge.ProductionPolicyAttempt) (*rp.LookupReply, bridge.Result, error)
	ObserveProductionPolicyProgress(context.Context, bridge.ProductionPolicyAttempt, *rp.Receipt) (*rp.ProgressReply, bridge.Result, error)
}
type ProductionPolicyWriter interface {
	ApplyProductionPolicy(context.Context, *a.WritePrecondition, bridge.ProductionPolicyTarget) (*op.ExecuteReply, bridge.Result, error)
}
type ProductionPolicyCapabilities struct {
	Native ProductionPolicyNative
	Writer ProductionPolicyWriter
}
type productionPolicyBoundary struct {
	*boundary.Boundary
	production ProductionPolicyCapabilities
}

// productionPolicyTarget converts a store admission (the exact
// floors/stopped replacement plus the Commitments/Drills rows to resend
// verbatim and the CAS token the write must match) into the bridge's wire
// target shape.
func productionPolicyTarget(admission store.ProductionPolicyAdmission) bridge.ProductionPolicyTarget {
	floors := make(map[policy.Resource]int64, len(admission.Floors))
	for _, row := range admission.Floors {
		floors[policy.Resource(row.Resource)] = row.Floor
	}
	commitments := make(map[policy.Resource]int64, len(admission.Commitments))
	for _, row := range admission.Commitments {
		commitments[policy.Resource(row.Resource)] = row.Count
	}
	stopped := make([]policy.Resource, 0, len(admission.Stopped))
	for _, name := range admission.Stopped {
		stopped = append(stopped, policy.Resource(name))
	}
	drills := make([]bridge.ProductionDrillTarget, 0, len(admission.Drills))
	for _, d := range admission.Drills {
		drills = append(drills, bridge.ProductionDrillTarget{BuildingDef: d.BuildingDef, ResourceDef: d.ResourceDef, X: d.X, Z: d.Z, StockTarget: d.StockTarget})
	}
	return bridge.ProductionPolicyTarget{Floors: floors, Commitments: commitments, Stopped: stopped, Drills: drills, ExpectedSnapshotToken: admission.SnapshotToken}
}

// InspectProductionPolicy reads the native production-policy snapshot's
// fresh CAS token plus its currently-owned Commitments/Drills rows (which
// must be resent verbatim on the eventual write, see
// domain.ProductionPolicyAction's doc comment), then previews the desired
// floors/stopped replacement against that snapshot: no held-map/emergency
// review is needed here since RoutineProductionPolicyPlanner dispatches at
// most one immediate write per routine step.
func (b *productionPolicyBoundary) InspectProductionPolicy(ctx context.Context, target executor.Target) (executor.ProductionPolicyInspection, error) {
	out := executor.ProductionPolicyInspection{StartedAt: b.Clock.Now()}
	value, ok := target.Action.ProductionPolicy()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.production.Native.ReadProductionPolicy(ctx, boundary.Identity(target.Snapshot))
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	commitments := make([]store.ProductionCommitment, 0, len(read.Commitments))
	for name, count := range read.Commitments {
		commitments = append(commitments, store.ProductionCommitment{Resource: string(name), Count: count})
	}
	sort.Slice(commitments, func(i, j int) bool { return commitments[i].Resource < commitments[j].Resource })
	drills := make([]store.ProductionDrillTarget, 0, len(read.Drills))
	for _, d := range read.Drills {
		drills = append(drills, store.ProductionDrillTarget{BuildingDef: d.DefName, ResourceDef: d.Resource, X: d.X, Z: d.Z, StockTarget: d.StockTarget})
	}
	desiredFloors := make(map[policy.Resource]int64, len(value.Floors()))
	for _, row := range value.Floors() {
		desiredFloors[policy.Resource(row.Resource)] = row.Floor
	}
	desiredStopped := make([]policy.Resource, 0, len(value.Stopped()))
	for _, name := range value.Stopped() {
		desiredStopped = append(desiredStopped, policy.Resource(name))
	}
	previewTarget := bridge.ProductionPolicyTarget{Floors: desiredFloors, Commitments: read.Commitments, Stopped: desiredStopped, Drills: drillsFromRead(read.Drills), ExpectedSnapshotToken: read.SnapshotToken}
	preview, _, err := b.production.Native.PreviewProductionPolicy(ctx, boundary.Identity(current), previewTarget)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() || v.Projected != nil {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	// The read and the preview may straddle ticks under a running clock
	// (#244): the read anchors the admission and the preview must be fresh
	// for it.
	if !domain.Tick(v.Context.GetTick()).FreshFor(domain.Tick(read.Context.GetTick())) {
		return out, executor.ErrHeld
	}
	out.Facts = policy.ProductionPolicyFacts{Snapshot: current, Tick: domain.Tick(v.Context.GetTick()), Floors: domain.Known(read.Floors), Stopped: domain.Known(read.Stopped), Token: read.SnapshotToken}
	out.Commitments, out.Drills = commitments, drills
	out.ObservedAt = b.Clock.Now()
	return out, nil
}

// drillsFromRead converts the native owned-drill rows into the wire target
// shape verbatim (StockTarget only; Recovered/Missing are read-only facts),
// so the preview/dispatch replacement leaves every retained facility's
// target untouched.
func drillsFromRead(rows []bridge.ProductionDrill) []bridge.ProductionDrillTarget {
	out := make([]bridge.ProductionDrillTarget, 0, len(rows))
	for _, d := range rows {
		out = append(out, bridge.ProductionDrillTarget{BuildingDef: d.DefName, ResourceDef: d.Resource, X: d.X, Z: d.Z, StockTarget: d.StockTarget})
	}
	return out
}

func (b *productionPolicyBoundary) productionPolicyAttempt(p executor.Placement, target bridge.ProductionPolicyTarget) bridge.ProductionPolicyAttempt {
	return bridge.ProductionPolicyAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Target: target}
}

func (b *productionPolicyBoundary) SetProductionPolicy(ctx context.Context, d executor.ProductionPolicyDispatch) (executor.Receipt, error) {
	p := d.Attempt
	_, ok := p.Action.ProductionPolicy()
	target := productionPolicyTarget(d.Admission)
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.production.Writer.ApplyProductionPolicy(ctx, pre, target)
		},
	)
}

func (b *productionPolicyBoundary) ObserveProductionPolicy(ctx context.Context, d executor.ProductionPolicyDispatch, current domain.GenerationSnapshot) (executor.ProductionPolicyEvidence, error) {
	p := d.Attempt
	out := executor.ProductionPolicyEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	target := productionPolicyTarget(d.Admission)
	w := b.productionPolicyAttempt(p, target)
	lookup, _, err := b.production.Native.LookupProductionPolicy(ctx, w)
	if err != nil {
		return out, err
	}
	admitted := lookup.GetReceipt()
	if admitted == nil {
		absent, err := boundary.Unadmitted(lookup, p, current)
		if err != nil {
			return out, err
		}
		out.Observation, out.Complete, out.ObservedAt = absent, true, b.Clock.Now()
		return out, nil
	}
	if err = boundary.Admission(admitted, p, b.Session); err != nil {
		return out, err
	}
	reply, _, err := b.production.Native.ObserveProductionPolicyProgress(ctx, w, admitted)
	if err != nil {
		return out, err
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, w.Attempt) {
		return out, executor.ErrEvidence
	}
	out.Observation.Snapshot, err = boundary.Context(v.Context, current)
	if err != nil {
		return out, err
	}
	if v.Context.GetTick() < int64(p.Tick) {
		return out, executor.ErrEvidence
	}
	out.Observation.Tick, out.Observation.Causality = domain.Tick(v.Context.GetTick()), domain.AfterDispatch
	switch effect := v.Effect.(type) {
	case *rp.Progress_Completed:
		if !v.GetCompleteInspection() {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectCompleted
		out.Complete = true
	case *rp.Progress_Unsuccessful:
		if !v.GetCompleteInspection() {
			return out, executor.ErrEvidence
		}
		reason := map[rp.UnsuccessfulReason]domain.UnsuccessfulReason{rp.UnsuccessfulReason_UNSUCCESSFUL_REASON_NATIVE_FAILURE: domain.NativeFailure, rp.UnsuccessfulReason_UNSUCCESSFUL_REASON_CANCELLED: domain.NativeCancelled, rp.UnsuccessfulReason_UNSUCCESSFUL_REASON_INTERRUPTED: domain.NativeInterrupted, rp.UnsuccessfulReason_UNSUCCESSFUL_REASON_EXPIRED: domain.NativeExpired, rp.UnsuccessfulReason_UNSUCCESSFUL_REASON_TARGET_DEAD: domain.TargetDead, rp.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED: domain.OutcomeNotAchieved}[effect.Unsuccessful.GetReason()]
		if reason == "" {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = reason
		out.Complete = true
	case *rp.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}

var _ executor.ProductionPolicyBoundary = (*productionPolicyBoundary)(nil)
