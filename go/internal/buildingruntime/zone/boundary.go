package zone

import (
	"context"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/buildingruntime/boundary"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/executor"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/store"
	a "github.com/davidarcher/RimGovernor/go/internal/wire/authoritypb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	op "github.com/davidarcher/RimGovernor/go/internal/wire/operationspb"
	r "github.com/davidarcher/RimGovernor/go/internal/wire/receiptspb"
	"google.golang.org/protobuf/proto"
)

type ZoneNative interface {
	ReadZoneTarget(context.Context, *c.Identity, domain.ZoneCreate) (bridge.ZoneRead, bridge.Result, error)
	PreviewZone(context.Context, *c.Identity, bridge.ZoneTarget) (*op.PreviewReply, bridge.Result, error)
	ReadEmergency(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	LookupZone(context.Context, bridge.ZoneAttempt) (*r.LookupReply, bridge.Result, error)
	ObserveZone(context.Context, bridge.ZoneAttempt, *r.Receipt) (*r.ProgressReply, bridge.Result, error)
}
type ZoneWriter interface {
	CreateZone(context.Context, *a.WritePrecondition, bridge.ZoneTarget) (*op.ExecuteReply, bridge.Result, error)
}
type ZoneCapabilities struct {
	Native ZoneNative
	Writer ZoneWriter
}
type ZoneBoundary struct {
	*boundary.Boundary
	zone    ZoneCapabilities
	journal *store.Store
}

func NewZoneBoundary(base *boundary.Boundary, zone ZoneCapabilities, journal *store.Store) *ZoneBoundary {
	return &ZoneBoundary{Boundary: base, zone: zone, journal: journal}
}

func (b *ZoneBoundary) InspectZone(ctx context.Context, target executor.Target) (executor.ZoneInspection, error) {
	out := executor.ZoneInspection{StartedAt: b.Clock.Now()}
	zone, ok := target.Action.ZoneCreate()
	if !ok {
		return out, executor.ErrEvidence
	}
	read, _, err := b.zone.Native.ReadZoneTarget(ctx, boundary.Identity(target.Snapshot), zone)
	if err != nil {
		return out, err
	}
	current, err := boundary.Context(read.Context, target.Snapshot)
	if err != nil {
		return out, err
	}
	selected := bridge.ZoneTarget{Zone: zone, Token: read.Token}
	preview, _, err := b.zone.Native.PreviewZone(ctx, boundary.Identity(current), selected)
	if err != nil {
		return out, err
	}
	v := preview.GetEvaluated()
	if v == nil || !v.GetAccepted() {
		return out, executor.ErrHeld
	}
	if v.Projected != nil {
		return out, executor.ErrEvidence
	}
	if _, err = boundary.Context(v.Context, current); err != nil {
		return out, err
	}
	emergency, _, err := b.zone.Native.ReadEmergency(ctx, boundary.Identity(current))
	if err != nil {
		return out, err
	}
	if _, err = boundary.Context(emergency.Context, current); err != nil {
		return out, err
	}
	// The three reads need only be ordered, not simultaneous: the zone target's token already binds the create to the cells the read listed,
	// so a tick that advanced between them is a running clock, not stale
	// evidence. Demanding one tick held every dispatch until the game
	// paused, starving the Worker under 2500-tick windows (#150); supply
	// and the building family accept the same monotonic order.
	if v.Context.GetTick() < read.Context.GetTick() || !domain.Tick(emergency.Context.GetTick()).Covers(domain.Tick(read.Context.GetTick())) {
		return out, executor.ErrHeld
	}
	out.Current, out.Tick, out.Zone, out.SnapshotToken, out.Accepted = current, domain.Tick(v.Context.GetTick()), zone, selected.Token, true
	out.Emergency, err = policy.NewEmergencySnapshot(current, out.Tick, emergency.Facts)
	out.ObservedAt = b.Clock.Now()
	return out, err
}
func (b *ZoneBoundary) zoneAttempt(ctx context.Context, p executor.Placement) (bridge.ZoneAttempt, error) {
	zone, _ := p.Action.ZoneCreate()
	state, err := b.journal.LoadPlan(ctx, p.Snapshot.Plan)
	if err != nil {
		return bridge.ZoneAttempt{}, err
	}
	for _, row := range state.ZoneAdmissions {
		if row.Action == p.Action.ID() && row.Admission.Snapshot == p.Snapshot {
			return bridge.ZoneAttempt{Identity: boundary.Identity(p.Snapshot), Attempt: b.Attempt(p), Generation: uint64(p.Snapshot.Native), Token: row.Admission.SnapshotToken, Zone: zone}, nil
		}
	}
	return bridge.ZoneAttempt{}, executor.ErrEvidence
}
func (b *ZoneBoundary) CreateZone(ctx context.Context, request executor.ZoneDispatch) (executor.Receipt, error) {
	p := request.Attempt
	zone, ok := p.Action.ZoneCreate()
	return b.DispatchWrite(ctx, p,
		func() error {
			if !ok {
				return executor.ErrEvidence
			}
			return nil
		},
		func(pre *a.WritePrecondition) (*op.ExecuteReply, bridge.Result, error) {
			return b.zone.Writer.CreateZone(ctx, pre, bridge.ZoneTarget{Zone: zone, Token: request.SnapshotToken})
		},
	)
}
func (b *ZoneBoundary) ObserveZone(ctx context.Context, p executor.Placement, current domain.GenerationSnapshot) (executor.ZoneEvidence, error) {
	out := executor.ZoneEvidence{StartedAt: b.Clock.Now(), Observation: domain.Observation{Action: p.Action.ID(), Attempt: p.Attempt, Snapshot: current, Effect: domain.EffectUnknown}}
	if !boundary.World(current, p.Snapshot) {
		return out, executor.ErrAuthority
	}
	w, err := b.zoneAttempt(ctx, p)
	if err != nil {
		return out, err
	}
	lookup, _, err := b.zone.Native.LookupZone(ctx, w)
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
	reply, _, err := b.zone.Native.ObserveZone(ctx, w, admitted)
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
	out.Complete, out.Zone = v.GetCompleteInspection(), w.Zone
	var evidence *r.EffectEvidence
	switch effect := v.Effect.(type) {
	case *r.Progress_Completed:
		evidence = effect.Completed.GetEvidence()
		out.Observation.Effect = domain.EffectCompleted
		// The receipt's zone_id is the identity later censuses name the
		// zone by (ZoneMatches has validated it).
		out.Observation.Zone = evidence.GetZone().GetZoneId()
	case *r.Progress_Unsuccessful:
		if effect.Unsuccessful.GetReason() != r.UnsuccessfulReason_UNSUCCESSFUL_REASON_OUTCOME_NOT_ACHIEVED {
			return out, executor.ErrEvidence
		}
		evidence = effect.Unsuccessful.GetEvidence()
		out.Observation.Effect = domain.EffectUnsuccessful
		out.Observation.UnsuccessfulReason = domain.OutcomeNotAchieved
	case *r.Progress_Unknown:
	default:
		return out, executor.ErrEvidence
	}
	if evidence != nil {
		matches, err := bridge.ZoneMatches(evidence, w.Zone, w.Token)
		if err != nil {
			return out, executor.ErrEvidence
		}
		out.Matches = domain.Known(matches)
	}
	out.ObservedAt = b.Clock.Now()
	return out, nil
}
