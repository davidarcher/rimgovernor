// Package coverclearance is the defense layout's cover-clearance executor
// boundary (#581): the cut-clearance boundary's shape over the defense site
// census (one cell, the target's own) and the ClearCover designation.
package coverclearance

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

type CoverClearanceNative interface {
	ReadDefenseSite(context.Context, *c.Identity, bridge.CellRect) (bridge.DefenseSite, bridge.Result, error)
	PreviewCoverClearance(context.Context, *c.Identity, bridge.CoverClearanceTarget) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupCoverClearance(context.Context, bridge.CoverClearanceAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveCoverClearance(context.Context, bridge.CoverClearanceAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type CoverClearanceWriter interface {
	DesignateCoverClearance(context.Context, *a.WritePrecondition, bridge.CoverClearanceTarget) (*op.ExecuteReply, bridge.Result, error)
}
type CoverClearanceCapabilities struct {
	Native CoverClearanceNative
	Writer CoverClearanceWriter
}
type CoverClearanceBoundary struct {
	*boundary.Boundary
	coverClearance CoverClearanceCapabilities
}

func NewCoverClearanceBoundary(base *boundary.Boundary, coverClearance CoverClearanceCapabilities) *CoverClearanceBoundary {
	return &CoverClearanceBoundary{Boundary: base, coverClearance: coverClearance}
}

func (b *CoverClearanceBoundary) InspectCoverClearance(ctx context.Context, target executor.Target) (executor.CoverClearanceInspection, error) {
	out := executor.CoverClearanceInspection{StartedAt: b.Clock.Now()}
	clearance, ok := target.Action.CoverClearance()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.coverClearance.Native.ReadDefenseSite(ctx, boundary.Identity(target.Snapshot), bridge.CellRect{Min: clearance.Cell(), Max: clearance.Cell()})
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	var selected bridge.CoverClearanceTarget
	for _, item := range read.Cells {
		if item.Cell != clearance.Cell() || item.Cover == nil || item.Cover.ThingID != clearance.Thing() {
			continue
		}
		if selected.Token != "" {
			return out, executor.ErrEvidence
		}
		if !item.Cover.Designated && item.Cover.DefName == clearance.Definition() && item.Cover.Designation() == clearance.Designation() {
			selected = bridge.CoverClearanceTarget{Clearance: clearance, Token: item.Cover.Token}
		}
	}
	if selected.Clearance != clearance || selected.Token == "" {
		// The read is fresh and bound to this colony/load/map: the exact
		// thing is no longer undesignated cover at its cell.
		return out, executor.ErrCoverClearanceAbsent
	}
	preview, _, err := b.coverClearance.Native.PreviewCoverClearance(ctx, boundary.Identity(current), selected)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return out, executor.ErrHeld
	}
	if !boundaryCoverClearanceEffect(v.Projected, clearance, true) {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	emergency, _, err := b.coverClearance.Native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	// The three reads need only be ordered, not simultaneous: the thing's
	// snapshot token already binds the designation to the thing the census
	// listed, so a tick that advanced between them is a running clock, not
	// stale evidence (the supply family's #120 rule). The emergency read may
	// come from the step's fact cache under a running window (#243), so it
	// may predate the preview by the emergency family's tick tolerance and
	// no more; the work is designated, not ordered, so a threat that far
	// back is the clock scheduler's to stop on.
	if v.Context.GetTick() < read.Context.GetTick() || bridge.FactEmergency.Outrun(emergency.Context.GetTick(), v.Context.GetTick()) {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Clearance, out.SnapshotToken, out.Accepted = current, domain.Tick(v.Context.GetTick()), clearance, selected.Token, true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *CoverClearanceBoundary) coverClearanceAttempt(p executor.Placement) bridge.CoverClearanceAttempt {
	clearance, _ := p.Action.CoverClearance()
	return bridge.CoverClearanceAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Clearance: clearance}
}
func (b *CoverClearanceBoundary) DesignateCoverClearance(ctx context.Context, request executor.CoverClearanceDispatch) (executor.Receipt, error) {
	p := request.Attempt
	clearance, ok := p.Action.CoverClearance()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.coverClearance.Writer.DesignateCoverClearance(ctx, pre, bridge.CoverClearanceTarget{Clearance: clearance, Token: request.SnapshotToken})
		},
	)
}
func (b *CoverClearanceBoundary) ObserveCoverClearance(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.CoverClearanceEvidence, error) {
	out := executor.CoverClearanceEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w := b.coverClearanceAttempt(p)
	lookup, _, err := b.coverClearance.Native.LookupCoverClearance(ctx, w)
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
	reply, _, err := b.coverClearance.Native.ObserveCoverClearance(ctx, w, admitted)
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
	out.Complete, out.Clearance = v.GetCompleteInspection(), w.Clearance
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		if !v.GetCompleteInspection() || !boundaryCoverClearanceEffect(effect.Completed.GetEvidence(), w.Clearance, true) {
			return out, executor.ErrEvidence
		}
		out.Observation.Effect, out.Designated = domain.EffectCompleted, domain.Known(effect.Completed.GetEvidence().GetDesignation().GetPresent())
	case *r.Progress_Unsuccessful:
		if !v.GetCompleteInspection() || effect.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED || !boundaryCoverClearanceEffect(effect.Unsuccessful.GetEvidence(), w.Clearance, false) {
			return out, executor.ErrEvidence
		}
		out.Designated = domain.Known(false)
		out.Observation.Effect, out.Observation.UnsuccessfulReason = domain.EffectUnsuccessful, domain.OutcomeNotAchieved
	case *r.Progress_Pending:
		// The thing still stands with its designation: its removal is pending
		// ordinary work, and the executor observes again.
		if !v.GetCompleteInspection() || !boundaryCoverClearanceEffect(effect.Pending.GetEvidence(), w.Clearance, true) {
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

func boundaryCoverClearanceEffect(v *r.EffectEvidence, clearance domain.CoverClearance, designated bool) bool {
	d := v.GetDesignation()
	return d != nil && d.Present != nil && d.GetPresent() == designated && d.GetThingId() == clearance.Thing() && d.GetResourceDef() == clearance.Definition() && d.GetDesignationDef() == clearance.Designation() && d.Cell != nil && d.Cell.X != nil && d.Cell.Z != nil && d.Cell.GetX() == clearance.Cell().X && d.Cell.GetZ() == clearance.Cell().Z
}
