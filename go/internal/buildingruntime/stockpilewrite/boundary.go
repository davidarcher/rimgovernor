// Package stockpilewrite wires the two stockpile write kinds, a zone cell
// edit (native EditZoneCells) and a stockpile patch (native PatchStockpile
// on a zone or a player storage building), through the one-shot CAS
// settings-write shape internal/buildingruntime/zonedelete uses: a fresh
// target token read, native preview, emergency check, then a direct
// write/lookup/observe. The native progress verdict (settings or cells
// match the admitted body) is the completion evidence.
package stockpilewrite

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type Native interface {
	ReadZoneDeleteTarget(context.Context, *c.Identity, string) (bridge.ZoneDeleteTarget, bridge.Result, error)
	ReadStorageBuildingTarget(context.Context, *c.Identity, string) (bridge.StorageBuildingTarget, bridge.Result, error)
	PreviewStockpileWrite(context.Context, *c.Identity, domain.Action) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupStockpileWrite(context.Context, bridge.StockpileWriteAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveStockpileWrite(context.Context, bridge.StockpileWriteAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type Writer interface {
	ApplyStockpileWrite(context.Context, *a.WritePrecondition, domain.Action) (*op.ExecuteReply, bridge.Result, error)
}
type Capabilities struct {
	Native Native
	Writer Writer
}
type Boundary struct {
	*boundary.Boundary
	w Capabilities
}

func NewBoundary(base *boundary.Boundary, w Capabilities) *Boundary {
	return &Boundary{Boundary: base, w: w}
}

// readToken reads the write target's live CAS token: the per-zone token for
// a cell edit or zone patch, the storage token for a storage building.
func (b *Boundary) readToken(ctx context.Context, action domain.Action, s domain.GenerationSnapshot) (present bool, token string, tick domain.Tick, err error) {
	target, _, ok := bridge.StockpileWriteTarget(action)
	if !ok {
		return false, "", 0, executor.ErrEvidence
	}
	var context *c.ObservationContext
	if p, isPatch := action.StockpilePatch(); isPatch && p.TargetKind() == domain.StorageBuildingTarget {
		row, _, err := b.w.Native.ReadStorageBuildingTarget(ctx, boundary.Identity(s), target)
		if err != nil {
			return false, "", 0, err
		}
		if row.Thing != target {
			return false, "", 0, executor.ErrHeld
		}
		context, present, token = row.Context, row.Present, row.Token
	} else {
		row, _, err := b.w.Native.ReadZoneDeleteTarget(ctx, boundary.Identity(s), target)
		if err != nil {
			return false, "", 0, err
		}
		if row.Zone != target {
			return false, "", 0, executor.ErrHeld
		}
		context, present, token = row.Context, row.Present, row.Token
	}
	if _, err = boundary.Context(context, s); err != nil {
		return false, "", 0, err
	}
	return present, token, domain.Tick(context.GetTick()), nil
}

func (b *Boundary) InspectZoneWrite(ctx context.Context, t executor.Target) (executor.ZoneWriteInspection, error) {
	out := executor.ZoneWriteInspection{StartedAt: b.Clock.Now()}
	_, before, ok := bridge.StockpileWriteTarget(t.Action)
	if !ok {
		return out, executor.ErrEvidence
	}
	present, token, tick, err := b.readToken(ctx, t.Action, t.Snapshot)
	if err != nil {
		return out, err
	}
	if !present || token != before {
		return out, executor.ErrHeld
	}
	preview, _, err := b.w.Native.PreviewStockpileWrite(ctx, boundary.Identity(t.Snapshot), t.Action)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return out, executor.ErrHeld
	}
	if _, err = boundary.Context(v.Context, t.Snapshot); err != nil {
		return out, err
	}
	emergency, _, err := b.w.Native.ReadEmergency(ctx, boundary.Identity(t.Snapshot))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, t.Snapshot); err != nil {
		return out, err
	}
	// Ordered reads, not one tick: the before-token binds the write to the
	// state the read listed (#195).
	if v.Context.GetTick() < int64(tick) {
		return out, executor.ErrHeld
	}
	tick = domain.Tick(v.Context.GetTick())
	out.Current, out.Tick, out.Action, out.SnapshotToken, out.Accepted = t.Snapshot, tick, t.Action, before, true
	out.Emergency, err = policy.NewEmergencySnapshot(t.Snapshot, tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}

func (b *Boundary) attempt(p executor.Placement) bridge.StockpileWriteAttempt {
	return bridge.StockpileWriteAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Action: p.Action}
}

func (b *Boundary) ApplyZoneWrite(ctx context.Context, d executor.ZoneWriteDispatch) (executor.Receipt, error) {
	p := d.Attempt
	_, before, ok := bridge.StockpileWriteTarget(p.Action)
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok || d.SnapshotToken != before {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.w.Writer.ApplyStockpileWrite(ctx, pre, p.Action)
		},
	)
}

func (b *Boundary) ObserveZoneWrite(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.ZoneWriteEvidence, error) {
	out := executor.ZoneWriteEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	wanted := b.attempt(p)
	lookup, _, err := b.w.Native.LookupStockpileWrite(ctx, wanted)
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
	reply, _, err := b.w.Native.ObserveStockpileWrite(ctx, wanted, admitted)
	if err != nil {
		return out, err
	}
	v := reply.GetProgress()
	if v == nil || !proto.Equal(v.Attempt, wanted.Attempt) {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	if v.Context.GetTick() < int64(p.Tick) {
		return out, executor.ErrEvidence
	}
	out.Observation.Tick, out.Observation.Causality = domain.Tick(v.Context.GetTick()), domain.AfterDispatch
	out.Action = p.Action
	if v.GetUnknown() != nil {
		out.ObservedAt = b.Clock.Now()
		return out, nil
	}
	if !v.GetCompleteInspection() {
		return out, executor.ErrEvidence
	}
	var matches bool
	if v.GetCompleted() != nil {
		matches = true
		out.Observation.Effect = domain.EffectCompleted
	} else if failed := v.GetUnsuccessful(); failed != nil && failed.GetReason() == r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
	} else {
		return out, executor.ErrEvidence
	}
	out.Complete, out.Matches, out.ObservedAt = true, domain.Known(matches), b.Clock.Now()
	return out, nil
}

var _ executor.ZoneWriteBoundary = (*Boundary)(nil)
