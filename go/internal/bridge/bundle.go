package bridge

import (
	"context"
	"errors"

	k "github.com/davidarcher/RimGovernor/go/internal/wire/clockpb"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
)

const bundleMethod = "rimgovernor/observations_read_bundle"

// ReadBundle is the one native read of a scheduler step or event poll: the
// bare observation scope (what Tick reports) plus, as requested, the owned
// clock status, the emergency status and one clock events page, all taken
// in a single main-thread hop so they describe one tick. Each section is
// validated exactly as its dedicated read would be; a clock status the
// native reports unavailable is the same ErrUnavailable ReadClockStatus
// returns. Without a scope the bundle describes the current map.
//
// The reply is never memoized as a whole (its clock sections are live
// controller state), but its tick and emergency sections are seeded into
// the step cache under the keys Tick and ReadEmergency use, so the routine
// census and the planners read them without another round trip and the
// cross-step FactCache files them under their own families (identity,
// emergency), where the typed events PollEvents commits discard them.
//
// A planning step also asks for the census families it would otherwise
// read one by one after the bundle (colony facts, population, research and
// the colonists' routine pawn detail, issue #180). Each rides in the same
// hop and is seeded under the exact key its dedicated read uses; the
// native omits a family it cannot read or fit, so a requested family may
// be absent and its read then goes natively, exactly as before.
func (client *Client) ReadBundle(ctx context.Context, request *o.BundleRequest) (*o.BundleReply, Result, error) {
	if request == nil {
		return nil, Result{}, contract("bundle request required")
	}
	request = proto.Clone(request).(*o.BundleRequest)
	if request.Scope != nil {
		if err := ValidateIdentity(request.Scope.ExpectedIdentity); err != nil {
			return nil, Result{}, err
		}
	}
	if events := request.Events; events != nil {
		if events.AfterCursor == nil || events.GetAfterCursor() < 0 || events.Limit == nil || events.GetLimit() < 1 || events.GetLimit() > 128 {
			return nil, Result{}, contract("bundle events cursor/limit")
		}
		if events.GetWaitMs() > ClockEventsMaxWaitMs {
			return nil, Result{}, contract("bundle events wait bound")
		}
	}
	if request.GetColonistPawns() && !request.GetEmergency() {
		return nil, Result{}, contract("bundle colonist pawns require the emergency section")
	}
	reply := &o.BundleReply{}
	raw, err := client.protoRead(ctx, bundleMethod, request, reply)
	if err != nil {
		return nil, raw, err
	}
	switch v := reply.Outcome.(type) {
	case *o.BundleReply_Observed:
		err = client.bundleObserved(ctx, request, v.Observed, raw)
	case *o.BundleReply_Unavailable:
		err = unavailable(v.Unavailable, raw)
	case *o.BundleReply_Failure:
		err = failure(v.Failure, raw)
	default:
		err = contract("bundle outcome missing")
	}
	if err != nil && !errors.Is(err, ErrRefused) && !errors.Is(err, ErrUnavailable) {
		return nil, raw, err
	}
	return reply, raw, err
}

func (client *Client) bundleObserved(ctx context.Context, request *o.BundleRequest, v *o.BundleSnapshot, raw Result) error {
	if v == nil {
		return contract("bundle snapshot missing")
	}
	if err := ValidateContext(v.Context); err != nil {
		return err
	}
	identity := v.Context.Identity
	if request.Scope != nil && !sameIdentity(identity, request.Scope.ExpectedIdentity) {
		return contract("bundle identity mismatch")
	}
	if v.Paused == nil {
		return contract("bundle pause state missing")
	}
	if request.GetClockStatus() != (v.ClockStatus != nil) || request.GetEmergency() != (v.Emergency != nil) || (request.Events != nil) != (v.Events != nil) {
		return contract("bundle sections differ from the request")
	}
	if !request.GetColonyFacts() && v.ColonyFacts != nil || !request.GetPopulation() && v.Population != nil || !request.GetResearch() && v.Research != nil || !request.GetColonistPawns() && v.ColonistPawns != nil {
		return contract("bundle carries an unrequested family")
	}
	for _, family := range []struct {
		present bool
		context *c.ObservationContext
	}{{v.ColonyFacts != nil, v.ColonyFacts.GetContext()}, {v.Population != nil, v.Population.GetContext()}, {v.Research != nil, v.Research.GetContext()}, {v.ColonistPawns != nil, v.ColonistPawns.GetContext()}} {
		if !family.present {
			continue
		}
		if err := ValidateContext(family.context); err != nil {
			return err
		}
		if !sameIdentity(family.context.Identity, identity) || family.context.GetTick() != v.Context.GetTick() {
			return contract("bundle family context mismatch")
		}
	}
	if v.ClockStatus != nil {
		if err := clockStatus(v.ClockStatus, identity); err != nil {
			return err
		}
		if !sameIdentity(v.ClockStatus.Context.Identity, identity) || v.ClockStatus.Context.GetTick() != v.Context.GetTick() {
			return contract("bundle clock status context mismatch")
		}
	}
	var emergency EmergencyObservation
	if v.Emergency != nil {
		var err error
		if emergency, err = DecodeEmergencyStatus(v.Emergency, identity); err != nil {
			return err
		}
		if v.Emergency.Context.GetTick() != v.Context.GetTick() {
			return contract("bundle emergency context mismatch")
		}
	}
	if v.Events != nil {
		if err := clockEventsPage(v.Events, BundleEventsRequest(identity, request.Events)); err != nil {
			return err
		}
		if v.Events.Context.GetTick() != v.Context.GetTick() {
			return contract("bundle events context mismatch")
		}
	}
	if err := validateBundleStepFamilies(request, v); err != nil {
		return err
	}
	if err := validateBundleView(request, v); err != nil {
		return err
	}
	client.seedBundle(ctx, request, v, emergency, raw)
	if v.ClockStatus != nil && v.ClockStatus.GetUnavailable() != nil {
		return unavailable(v.ClockStatus.GetUnavailable(), raw)
	}
	return nil
}

// BundleEventsRequest is the clock events request a bundle's events section
// answers, for the validators and the journal append that take one.
func BundleEventsRequest(identity *c.Identity, events *o.BundleEventsRequest) *k.EventsRequest {
	request := &k.EventsRequest{Identity: proto.Clone(identity).(*c.Identity), AfterCursor: proto.Int64(events.GetAfterCursor()), Limit: proto.Uint32(events.GetLimit())}
	if events.GetWaitMs() > 0 {
		request.WaitMs = proto.Uint32(events.GetWaitMs())
	}
	return request
}

// BundleEmergency decodes a bundle's emergency section into the observation
// ReadEmergency returns.
func BundleEmergency(v *o.BundleSnapshot) (EmergencyObservation, error) {
	if v == nil || v.Emergency == nil {
		return EmergencyObservation{}, contract("bundle emergency section missing")
	}
	return DecodeEmergencyStatus(v.Emergency, v.Context.GetIdentity())
}

// seedBundle files the bundle's cacheable sections in the step cache ctx
// carries, if any, as the replies their dedicated reads would have stored.
// The colonist pawns section is keyed by the request ReadRoutinePawns
// builds from the same emergency section (the colonists' ids in census
// order, only when that census is complete), so the routine bracket's read
// is the hit.
func (client *Client) seedBundle(ctx context.Context, request *o.BundleRequest, v *o.BundleSnapshot, emergency EmergencyObservation, raw Result) {
	cache := StepReadCacheFrom(ctx)
	if cache == nil {
		return
	}
	scope, ok := replyScope(&l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: v.Context, Paused: v.Paused}}})
	if !ok {
		return
	}
	seed := func(method string, request, reply proto.Message) {
		encoded, err := proto.MarshalOptions{Deterministic: true}.Marshal(request)
		if err != nil {
			return
		}
		payload, err := proto.Marshal(reply)
		if err != nil {
			return
		}
		cache.seed(readCacheKey{method: method, request: string(encoded)}, scope, payload, raw)
	}
	seed("rimgovernor/lifecycle_read_tick", &l.TickRequest{}, &l.TickReply{Outcome: &l.TickReply_Loaded{Loaded: &l.LoadedTick{Context: v.Context, Paused: v.Paused}}})
	identity := v.Context.Identity
	if v.Emergency != nil {
		seed("rimgovernor/observations_read_status", emergencyRequest(identity), &o.StatusReply{Outcome: &o.StatusReply_Observed{Observed: v.Emergency}})
	}
	if v.ColonyFacts != nil {
		seed("rimgovernor/observations_read_colony_facts", colonyFactsRequest(identity, true, nil), &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: v.ColonyFacts}})
	}
	if v.Population != nil && maskServes(request.GetPopulationFields(), seededPopulationFields) {
		seed("rimgovernor/observations_read_population", populationRequest(identity), &o.PopulationReply{Outcome: &o.PopulationReply_Observed{Observed: v.Population}})
	}
	if v.Research != nil && maskServes(request.GetResearchFields(), seededResearchFields) {
		seed("rimgovernor/observations_read_research", researchRequest(identity), &o.ResearchReply{Outcome: &o.ResearchReply_Observed{Observed: v.Research}})
	}
	if ids := routinePawnIDs(emergency); v.ColonistPawns != nil && len(ids) > 0 && maskServes(request.GetColonistPawnFields(), seededPawnFields) {
		seed("rimgovernor/observations_list_pawns", pawnDetailsRequest(identity, ids, pawnDetails{Combat: true, Work: true, Care: true, Schedule: true, Social: true}), &o.ListPawnsReply{Outcome: &o.ListPawnsReply_Observed{Observed: v.ColonistPawns}})
	}
	client.seedBundleStepFamilies(ctx, request, v, seed)
}

// The optional blocks (#360) the consumers of each seeded key decode: every
// reader behind ReadRoutinePopulation/ReadPrisonerInteractionTarget,
// ReadResearch and ReadRoutinePawns. The routine pawn profile reads traits and
// the biological age (observation.routine_work, #695), so a pawn mask without
// them never seeds; the other masked blocks stay unread, and the routine
// bundle's slim families stand in for the dedicated reads. A consumer
// that starts decoding a block adds its flag here; a bundle whose mask omits
// it then stops seeding that key (#648), and the dedicated read serves the
// whole family instead of a slim copy passing for it.
var (
	seededPawnFields       = &o.PawnFields{IncludeTraits: proto.Bool(true), IncludeBackstory: proto.Bool(true)}
	seededPopulationFields = &o.PopulationFields{}
	seededResearchFields   = &o.ResearchFields{}
)

// maskServes reports whether a family answered under mask carries every
// optional block need sets. An absent mask is the whole family.
func maskServes(mask, need proto.Message) bool {
	if mask == nil || !mask.ProtoReflect().IsValid() {
		return true
	}
	have := mask.ProtoReflect()
	served := true
	need.ProtoReflect().Range(func(field protoreflect.FieldDescriptor, value protoreflect.Value) bool {
		if value.Bool() && !have.Get(field).Bool() {
			served = false
		}
		return served
	})
	return served
}

// routinePawnIDs lists the colonists the routine census reads pawn detail
// for, in census order: every colonist of a complete emergency census, none
// otherwise (the bracket then reads no pawns at all).
func routinePawnIDs(emergency EmergencyObservation) []string {
	if complete, known := emergency.Facts.ColonistsComplete.Value(); !known || !complete {
		return nil
	}
	ids := make([]string, 0, len(emergency.Facts.Colonists))
	for _, pawn := range emergency.Facts.Colonists {
		ids = append(ids, string(pawn.ID))
	}
	return ids
}
