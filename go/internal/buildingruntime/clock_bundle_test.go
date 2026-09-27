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
	mp "github.com/davidarcher/RimGovernor/go/internal/wire/mirrorpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// bundleParts are the dedicated reads a fake bundle or mirror poll is
// composed from, so a fake's ReadBundle and MirrorPoll answer exactly what
// its Tick, ReadClockStatus, ReadEmergency and ReadClockEvents answer
// (issue #127). A nil part leaves
// its section out of the fake's repertoire: a request for it is an error.
type bundleParts struct {
	tick      func(context.Context) (*l.TickReply, bridge.Result, error)
	status    func(context.Context, *c.Identity) (*k.StatusReply, bridge.Result, error)
	emergency func(context.Context, *c.Identity) (bridge.EmergencyObservation, bridge.Result, error)
	events    func(context.Context, *k.EventsRequest) (*k.EventsReply, bridge.Result, error)
}

func composeBundle(ctx context.Context, request *o.BundleRequest, parts bundleParts) (*o.BundleReply, bridge.Result, error) {
	tick, raw, err := parts.tick(ctx)
	if err != nil {
		return nil, raw, err
	}
	loaded := tick.GetLoaded()
	if loaded == nil {
		return nil, raw, errors.New("bundle: tick not loaded")
	}
	if request.Scope != nil && !proto.Equal(request.Scope.ExpectedIdentity, loaded.Context.Identity) {
		return nil, raw, bridge.ErrRefused
	}
	paused := loaded.Paused
	if paused == nil {
		paused = proto.Bool(false)
	}
	observed := &o.BundleSnapshot{Context: proto.Clone(loaded.Context).(*c.ObservationContext), Paused: paused}
	if request.GetClockStatus() {
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
	if request.GetEmergency() {
		if parts.emergency == nil {
			return nil, raw, errors.New("bundle: emergency not served")
		}
		emergency, _, err := parts.emergency(ctx, loaded.Context.Identity)
		if err != nil {
			return nil, raw, err
		}
		observed.Emergency = emergencySnapshot(observed.Context, emergency.Facts)
	}
	if request.Events != nil {
		return nil, raw, errors.New("bundle: events are read through mirror_poll")
	}
	return &o.BundleReply{Outcome: &o.BundleReply_Observed{Observed: observed}}, raw, nil
}

// composeMirrorPoll answers a mirror poll the way composeBundle answers a
// bundle: the journal page from parts.events for the scope parts.tick
// reports, and no mirror sections.
func composeMirrorPoll(ctx context.Context, request *mp.MirrorPollRequest, parts bundleParts) (*mp.MirrorPollReply, bridge.Result, error) {
	tick, raw, err := parts.tick(ctx)
	if err != nil {
		return nil, raw, err
	}
	loaded := tick.GetLoaded()
	if loaded == nil {
		return nil, raw, errors.New("mirror poll: tick not loaded")
	}
	if request.Identity != nil && !proto.Equal(request.Identity, loaded.Context.Identity) {
		return nil, raw, bridge.ErrRefused
	}
	if parts.events == nil {
		return nil, raw, errors.New("mirror poll: events not served")
	}
	events := bridge.MirrorJournalRequest(loaded.Context.Identity, request)
	if request.GetWaitMs() > 0 {
		events.WaitMs = proto.Uint32(request.GetWaitMs())
	}
	reply, _, err := parts.events(ctx, events)
	if err != nil {
		return nil, raw, err
	}
	if reply.GetPage() == nil {
		return nil, raw, errors.New("mirror poll: events page missing")
	}
	return &mp.MirrorPollReply{Outcome: &mp.MirrorPollReply_Page{Page: &mp.MirrorPage{Journal: reply.GetPage()}}}, raw, nil
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
		colonists.Pawns = append(colonists.Pawns, &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(string(pawn.ID))}, Dead: known(pawn.Dead), Downed: known(pawn.Downed), Health: &o.PawnHealth{Bleeding: known(pawn.Bleeding), NeedsTend: known(pawn.NeedsTend)}})
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
		}
	}
	return &o.StatusSnapshot{Context: proto.Clone(context).(*c.ObservationContext), Colonists: colonists, Threats: threats}
}
