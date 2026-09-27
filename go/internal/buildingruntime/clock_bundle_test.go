package buildingruntime

import (
	"context"
	"errors"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// bundleParts are the dedicated reads a fake step read is
// composed from, so a fake's ReadStep answers exactly what
// its Tick, ReadClockStatus, ReadEmergency and ReadClockEvents answer
// (issue #127). A nil part leaves
// its section out of the fake's repertoire: a request for it is an error.
type bundleParts struct {
	tick      func(context.Context) (*l.TickReply, bridge.Result, error)
	status    func(context.Context, *c.Identity) (*k.StatusReply, bridge.Result, error)
	emergency func(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	events    func(context.Context, *k.EventsRequest) (*k.EventsReply, bridge.Result, error)
}

func composeStep(ctx context.Context, request bridge.StepRequest, parts bundleParts) (*o.BundleSnapshot, bridge.Result, error) {
	tick, raw, err := parts.tick(ctx)
	if err != nil {
		return nil, raw, err
	}
	loaded := tick.GetLoaded()
	if loaded == nil {
		return nil, raw, errors.New("bundle: tick not loaded")
	}
	if request.Identity != nil && !proto.Equal(request.Identity, loaded.Context.Identity) {
		return nil, raw, bridge.ErrRefused
	}
	paused := loaded.Paused
	if paused == nil {
		paused = proto.Bool(false)
	}
	observed := &o.BundleSnapshot{Context: proto.Clone(loaded.Context).(*c.ObservationContext), Paused: paused}
	if request.ClockStatus {
		if parts.status == nil {
			return nil, raw, errors.New("bundle: clock status not served")
		}
		reply, _, err := parts.status(ctx, loaded.Context.Identity)
		if err != nil {
			return nil, raw, err
		}
		observed.ClockStatus = reply.GetStatus()
		if observed.ClockStatus == nil {
			return nil, raw, errors.New("bundle: clock status missing")
		}
		observed.Paused = proto.Bool(observed.ClockStatus.GetActualPaused())
	}
	if request.Emergency {
		if parts.emergency == nil {
			return nil, raw, errors.New("bundle: emergency not served")
		}
		emergency, _, err := parts.emergency(ctx, loaded.Context.Identity)
		if err != nil {
			return nil, raw, err
		}
		observed.Emergency = emergencySnapshot(observed.Context, emergency.Facts)
	}
	return observed, raw, nil
}

// emergencySnapshot encodes the facts a fake serves as the status snapshot
// bridge.BundleEmergency decodes them from.
func emergencySnapshot(context *c.ObservationContext, facts policy.EmergencyFacts) *o.StatusSnapshot {
	completeness := func(fact domain.Fact[bool], rows int) *o.Completeness {
		complete, known := fact.Value()
		if !known {
			return nil
		}
		return &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(complete)}, Matched: proto.Uint64(uint64(rows)), Returned: proto.Uint64(uint64(rows)), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	}
	known := func(fact domain.Fact[bool]) *bool {
		if v, ok := fact.Value(); ok {
			return proto.Bool(v)
		}
		return nil
	}
	colonists := &o.PawnSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Completeness: completeness(facts.ColonistsComplete, len(facts.Colonists))}
	for _, pawn := range facts.Colonists {
		row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(string(pawn.ID))}, Dead: known(pawn.Dead), Downed: known(pawn.Downed), Health: &o.PawnHealth{Bleeding: known(pawn.Bleeding), NeedsTend: known(pawn.NeedsTend)}}
		if mental, ok := pawn.MentalState.Value(); ok {
			row.MentalState, row.MentalStateIsAggro, row.MentalStateTicks = proto.String(mental.DefName), proto.Bool(mental.IsAggro), proto.Int32(mental.TicksInState)
		}
		colonists.Pawns = append(colonists.Pawns, row)
	}
	threats := &o.ThreatsSnapshot{Completeness: completeness(facts.ThreatsComplete, len(facts.Threats))}
	for _, threat := range facts.Threats {
		row := &o.ThreatPawn{Pawn: &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(string(threat.ID))}, Dead: known(threat.Dead), Downed: known(threat.Downed), Animal: known(threat.Animal)}}
		if distance, ok := threat.Distance.Value(); ok {
			row.Pawn.NearestColonistDistance = proto.Float64(distance)
		}
		switch threat.Kind {
		case policy.Hostile:
			threats.Hostiles = append(threats.Hostiles, row)
		case policy.HuntingPredator:
			threats.HuntingPredators = append(threats.HuntingPredators, row)
		case policy.IgnoredHunter:
			threats.IgnoredHunters = append(threats.IgnoredHunters, row)
		case policy.NearbyPredator:
			threats.WildPredatorsNear = append(threats.WildPredatorsNear, row)
		case policy.NearbyDowned:
			threats.DownedNear = append(threats.DownedNear, row)
		case policy.HostileBuilding:
			building := &o.ThreatBuilding{Building: &o.EntityRef{Id: proto.String(string(threat.ID)), DefName: proto.String(threat.Definition),
				Snapshot: &o.SnapshotRef{Context: proto.Clone(context).(*c.ObservationContext), EntityId: proto.String(string(threat.ID)), Token: proto.String(threat.SnapshotToken)}}}
			for _, cell := range threat.Cells {
				building.OccupiedCells = append(building.OccupiedCells, &c.Cell{X: proto.Int32(cell.X), Z: proto.Int32(cell.Z)})
			}
			if distance, ok := threat.Distance.Value(); ok {
				building.NearestColonistDistance = proto.Int32(int32(distance))
			}
			threats.HostileBuildings = append(threats.HostileBuildings, building)
		}
	}
	return &o.StatusSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Colonists: colonists, Threats: threats}
}
