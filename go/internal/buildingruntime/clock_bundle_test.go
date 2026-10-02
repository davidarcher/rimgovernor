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
		observed.Emergency, observed.Pawns = emergencySnapshot(observed.Context, emergency.Facts)
	}
	return observed, raw, nil
}

// emergencySnapshot encodes the facts a fake serves as the status snapshot
// bridge.BundleEmergency decodes them from.
func emergencySnapshot(context *c.ObservationContext, facts policy.EmergencyFacts) (*o.StatusSnapshot, *o.PawnSnapshot) {
	// The census references its pawns' rows in the pawn table (#1343).
	table := &o.PawnSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Completeness: &o.Completeness{}}
	rows := map[string]*o.PawnState{}
	ref := func(id policy.PawnID, row *o.PawnState) *o.EntityRef {
		if _, ok := rows[string(id)]; !ok {
			rows[string(id)] = row
			table.Pawns = append(table.Pawns, row)
		}
		return &o.EntityRef{Id: proto.String(string(id))}
	}
	known := func(fact domain.Fact[bool]) *bool {
		if v, ok := fact.Value(); ok {
			return proto.Bool(v)
		}
		return nil
	}
	var colonists []*o.EntityRef
	for _, pawn := range facts.Colonists {
		row := &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(string(pawn.ID))}, Dead: known(pawn.Dead), Downed: known(pawn.Downed), Health: &o.PawnHealth{Bleeding: known(pawn.Bleeding), NeedsTend: known(pawn.NeedsTend)}}
		if mental, ok := pawn.MentalState.Value(); ok {
			row.MentalState, row.MentalStateIsAggro, row.MentalStateTicks = proto.String(mental.DefName), proto.Bool(mental.IsAggro), proto.Int32(mental.TicksInState)
		}
		colonists = append(colonists, ref(pawn.ID, row))
	}
	threats := &o.ThreatsSnapshot{}
	for _, threat := range facts.Threats {
		row := &o.ThreatPawn{}
		if threat.Kind != policy.HostileBuilding {
			row.Pawn = ref(threat.ID, &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(string(threat.ID))}, Dead: known(threat.Dead), Downed: known(threat.Downed), Animal: known(threat.Animal)})
		}
		if distance, ok := threat.Distance.Value(); ok {
			row.NearestColonistDistance = proto.Float64(distance)
		}
		// The fact row bridge.ClassifyThreat gives threat.Kind; a nearby
		// threat needs a distance within the proximity radius.
		if threat.Kind == policy.NearbyPredator || threat.Kind == policy.NearbyDowned {
			if row.NearestColonistDistance == nil {
				row.NearestColonistDistance = proto.Float64(0)
			}
		}
		switch threat.Kind {
		case policy.Hostile:
			row.FactionHostile, row.Faction = proto.Bool(true), bridge.NewRef("Faction_1")
			if passive, ok := threat.Passive.Value(); ok {
				row.Passive = proto.Bool(passive)
			}
			threats.Pawns = append(threats.Pawns, row)
		case policy.HuntingPredator:
			row.PredatorHunt = proto.Bool(true)
			threats.Pawns = append(threats.Pawns, row)
		case policy.IgnoredHunter:
			row.PredatorHunt, row.Ours = proto.Bool(true), proto.Bool(true)
			threats.Pawns = append(threats.Pawns, row)
		case policy.NearbyPredator:
			row.Predator = proto.Bool(true)
			threats.Pawns = append(threats.Pawns, row)
		case policy.NearbyDowned:
			row.Downed = proto.Bool(true)
			threats.Pawns = append(threats.Pawns, row)
		case policy.HostileBuilding:
			building := &o.ThreatBuilding{Building: &o.EntityRef{Id: proto.String(string(threat.ID)), DefName: proto.String(threat.Definition),
				Snapshot: &o.SnapshotRef{Context: proto.Clone(context).(*c.ObservationContext), EntityId: proto.String(string(threat.ID)), Token: proto.String(threat.SnapshotToken)}}}
			building.Occupied = bridge.WireRect(threat.Cells)
			if distance, ok := threat.Distance.Value(); ok {
				building.NearestColonistDistance = proto.Int32(int32(distance))
			}
			threats.HostileBuildings = append(threats.HostileBuildings, building)
		}
	}
	status := &o.StatusSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Colonists: colonists, Threats: threats}
	// A census not known complete is one the read could not list.
	if complete, ok := facts.ColonistsComplete.Value(); !ok || !complete {
		status.Colonists = nil
		status.Issues = []*o.ReadIssue{{Field: proto.String("colonists"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}
	}
	return status, table
}
