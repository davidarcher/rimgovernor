// Package excavation wires the staged-excavation vertical
// (domain.ExcavationAction) into buildingruntime. It is deliberately
// separate from buildingruntime/mineacquisition: resource mining is keyed by
// a Mineable ThingID and completes when yield is produced under the
// surface-mining guard (MiningBlocker), whereas excavation is keyed by cell
// plus rock definition, admits roofed cells under a counterfactual roof
// support check, and completes when the cell is cleared.
package excavation

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

type ExcavationNative interface {
	ReadExcavationSite(context.Context, *c.Identity, []domain.Cell, domain.Cell) (bridge.ExcavationSite, bridge.Result, error)
	PreviewExcavation(context.Context, *c.Identity, bridge.ExcavationTarget) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupExcavation(context.Context, bridge.ExcavationAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveExcavation(context.Context, bridge.ExcavationAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type ExcavationWriter interface {
	Excavate(context.Context, *a.WritePrecondition, bridge.ExcavationTarget) (*op.ExecuteReply, bridge.Result, error)
}
type ExcavationCapabilities struct {
	Native ExcavationNative
	Writer ExcavationWriter
}
type ExcavationBoundary struct {
	*boundary.Boundary
	capabilities ExcavationCapabilities
}

func NewExcavationBoundary(base *boundary.Boundary, capabilities ExcavationCapabilities) *ExcavationBoundary {
	return &ExcavationBoundary{Boundary: base, capabilities: capabilities}
}

// accessCell for the dispatch-time inspection is the target cell itself:
// native resolves a rock access cell to a visible walkable neighbour, the
// way a miner actually stands. Planner-level site reads pass the corridor
// mouth instead.
func accessCell(excavation domain.Excavation) domain.Cell { return excavation.Cell() }

// SiteFacts projects one site read taken for exactly one cell onto the
// policy facts EvaluateExcavation needs. Fogged cells carry unknown rock and
// eligibility; support is the site-level answer for removing that one cell.
func SiteFacts(site bridge.ExcavationSite, current domain.GenerationSnapshot) (policy.ExcavationFacts, string) {
	facts := policy.ExcavationFacts{Snapshot: current, ObservationTick: domain.Tick(site.Context.GetTick()), Support: site.Support}
	if len(site.Cells) != 1 {
		return facts, ""
	}
	row := site.Cells[0]
	facts.Fogged = domain.Known(row.Fogged)
	if !row.Fogged {
		facts.Definition, facts.Eligible = domain.Known(row.Definition), domain.Known(row.Eligible)
	}
	facts.WorkerAvailable, facts.AccessReachable = domain.Known(site.WorkerAvailable), domain.Known(site.AccessReachable)
	return facts, row.Token
}

func (b *ExcavationBoundary) InspectExcavation(ctx context.Context, target executor.Target) (executor.ExcavationInspection, error) {
	out := executor.ExcavationInspection{StartedAt: b.Clock.Now()}
	excavation, ok := target.Action.Excavation()
	if !ok {
		return out, executor.ErrEvidence
	}
	site, _, err := b.capabilities.Native.ReadExcavationSite(ctx, boundary.Identity(target.Snapshot), []domain.Cell{excavation.Cell()}, accessCell(excavation))
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(site.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	facts, token := SiteFacts(site, current)
	out.Current, out.Tick, out.Excavation, out.Facts = current, domain.Tick(site.Context.GetTick()), excavation, facts
	// The preview is only meaningful for an admissible visible cell; policy
	// holds on everything else from the facts alone.
	if token != "" && !site.Cells[0].Fogged && site.Cells[0].Eligible && site.Cells[0].Definition == excavation.Definition() && site.Support == policy.ExcavationSupportSupported {
		preview, _, err := b.capabilities.Native.PreviewExcavation(ctx, boundary.Identity(current), bridge.ExcavationTarget{Excavation: excavation, Token: token})
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
		if v.Context.GetTick() != site.Context.GetTick() {
			return out, executor.ErrHeld
		}
		out.SnapshotToken = token
	}
	emergency, _, err := b.capabilities.Native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	if emergency.Context.GetTick() != site.Context.GetTick() {
		return out, executor.ErrHeld
	}
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *ExcavationBoundary) excavationAttempt(p executor.Placement) bridge.ExcavationAttempt {
	excavation, _ := p.Action.Excavation()
	return bridge.ExcavationAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Excavation: excavation}
}
func (b *ExcavationBoundary) Excavate(ctx context.Context, request executor.ExcavationDispatch) (executor.Receipt, error) {
	p := request.Attempt
	excavation, ok := p.Action.Excavation()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.capabilities.Writer.Excavate(ctx, pre, bridge.ExcavationTarget{Excavation: excavation, Token: request.SnapshotToken})
		},
	)
}
func (b *ExcavationBoundary) ObserveExcavation(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.ExcavationEvidence, error) {
	out := executor.ExcavationEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w := b.excavationAttempt(p)
	lookup, _, err := b.capabilities.Native.LookupExcavation(ctx, w)
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
	reply, _, err := b.capabilities.Native.ObserveExcavation(ctx, w, admitted)
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
	out.Complete, out.Excavation = v.GetCompleteInspection(), w.Excavation
	var evidence *r.EffectEvidence
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		evidence = effect.Completed.GetEvidence()
		out.Observation.Effect = domain.EffectCompleted
	case *r.Progress_Unsuccessful:
		if effect.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return out, executor.ErrEvidence
		}
		evidence = effect.Unsuccessful.GetEvidence()
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
	case *r.Progress_Pending:
		evidence = effect.Pending.GetEvidence()
		out.Observation.Effect = domain.EffectPending
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	if evidence != nil {
		if bridge.ValidateExcavationEffect(evidence, w.Excavation) != nil {
			return out, executor.ErrEvidence
		}
		d := evidence.GetExcavation()
		out.Designated, out.Cleared, out.Cancelled, out.Blocker = d.GetDesignated(), d.GetCleared(), d.GetCancelled(), d.GetBlocker()
	}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}

var _ executor.ExcavationBoundary = (*ExcavationBoundary)(nil)
