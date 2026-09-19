// Package cutplant is the blight responder's executor boundary (#245): the
// supply boundary's shape over the blighted-plant census and the CutPlant
// designation.
package cutplant

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

type CutPlantNative interface {
	ReadBlightedPlants(context.Context, *c.Identity) (bridge.CutPlantRead, bridge.Result, error)
	PreviewCutPlant(context.Context, *c.Identity, bridge.CutPlantTarget) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupCutPlant(context.Context, bridge.CutPlantAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveCutPlant(context.Context, bridge.CutPlantAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type CutPlantWriter interface {
	DesignateCutPlant(context.Context, *a.WritePrecondition, bridge.CutPlantTarget) (*op.ExecuteReply, bridge.Result, error)
}
type CutPlantCapabilities struct {
	Native CutPlantNative
	Writer CutPlantWriter
}
type CutPlantBoundary struct {
	*boundary.Boundary
	cutPlant CutPlantCapabilities
}

func NewCutPlantBoundary(base *boundary.Boundary, cutPlant CutPlantCapabilities) *CutPlantBoundary {
	return &CutPlantBoundary{Boundary: base, cutPlant: cutPlant}
}

func (b *CutPlantBoundary) InspectCutPlant(ctx context.Context, target executor.Target) (executor.CutPlantInspection, error) {
	out := executor.CutPlantInspection{StartedAt: b.Clock.Now()}
	plant, ok := target.Action.CutPlant()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.cutPlant.Native.ReadBlightedPlants(ctx, boundary.Identity(target.Snapshot))
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	var selected bridge.CutPlantTarget
	for _, item := range read.Targets {
		if item.Plant == plant {
			if selected.Token != "" {
				return out, executor.ErrEvidence
			}
			selected = item
		}
	}
	if selected.Plant != plant || selected.Token == "" {
		// The read is fresh and bound to this colony/load/map: the exact
		// plant is no longer an undesignated blighted plant at its cell.
		return out, executor.ErrCutPlantAbsent
	}
	preview, _, err := b.cutPlant.Native.PreviewCutPlant(ctx, boundary.Identity(current), selected)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return out, executor.ErrHeld
	}
	if !boundaryCutPlantEffect(v.Projected, plant, true) {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	emergency, _, err := b.cutPlant.Native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	// The three reads need only be ordered, not simultaneous: the plant's
	// snapshot token already binds the designation to the plant the census
	// listed, so a tick that advanced between them is a running clock, not
	// stale evidence (the supply family's #120 rule). The emergency read may
	// come from the step's fact cache under a running window (#243), so it
	// may predate the preview by the emergency family's tick tolerance and
	// no more; the cutter is designated, not ordered, so a threat that far
	// back is the clock scheduler's to stop on.
	if v.Context.GetTick() < read.Context.GetTick() || emergency.Context.GetTick()+bridge.FactEmergency.TickTolerance() < v.Context.GetTick() {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Plant, out.SnapshotToken, out.Accepted = current, domain.Tick(v.Context.GetTick()), plant, selected.Token, true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *CutPlantBoundary) cutPlantAttempt(p executor.Placement) bridge.CutPlantAttempt {
	plant, _ := p.Action.CutPlant()
	return bridge.CutPlantAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Plant: plant}
}
func (b *CutPlantBoundary) DesignateCutPlant(ctx context.Context, request executor.CutPlantDispatch) (executor.Receipt, error) {
	p := request.Attempt
	plant, ok := p.Action.CutPlant()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.cutPlant.Writer.DesignateCutPlant(ctx, pre, bridge.CutPlantTarget{Plant: plant, Token: request.SnapshotToken})
		},
	)
}
func (b *CutPlantBoundary) ObserveCutPlant(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.CutPlantEvidence, error) {
	out := executor.CutPlantEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w := b.cutPlantAttempt(p)
	lookup, _, err := b.cutPlant.Native.LookupCutPlant(ctx, w)
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
	reply, _, err := b.cutPlant.Native.ObserveCutPlant(ctx, w, admitted)
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
	out.Complete, out.Plant = v.GetCompleteInspection(), w.Plant
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() || !boundaryCutPlantEffect(effect.Completed.GetEvidence(), w.Plant, true) {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect, out.Designated = domain.EffectCompleted, domain.Known(effect.Completed.GetEvidence().GetDesignation().GetPresent())
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || effect.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED || !boundaryCutPlantEffect(effect.Unsuccessful.GetEvidence(), w.Plant, false) {
			return out, executor.ErrEvidence
		}
		out.Designated = domain.Known(false)
		out.Observation.Effect, out.Observation.UnsuccessfulReason = domain.EffectUnsuccessful, domain.OutcomeNotAchieved
	case *r.Progress_Pending:
		// The plant still stands with its designation: the cut is pending
		// ordinary plant-cutting work, and the executor observes again.
		if !v.GetCompleteInspection() || !boundaryCutPlantEffect(effect.Pending.GetEvidence(), w.Plant, true) {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect, out.Designated = domain.EffectPending, domain.Known(true)
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}

func boundaryCutPlantEffect(v *r.EffectEvidence, plant domain.CutPlant, designated bool) bool {
	d := v.GetDesignation()
	return d != nil && d.Present != nil && d.GetPresent() == designated && d.GetThingId() == plant.Plant() && d.GetResourceDef() == plant.Definition() && d.GetDesignationDef() == "CutPlant" && d.Cell != nil && d.Cell.X != nil && d.Cell.Z != nil && d.Cell.GetX() == plant.Cell().X && d.Cell.GetZ() == plant.Cell().Z
}
