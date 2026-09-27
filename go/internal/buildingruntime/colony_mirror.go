package buildingruntime

import (
	"context"
	"sync"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

type colonyFactsReader interface {
	ReadColonyFacts(context.Context, *c.Identity, bool, []string) (*o.ColonyFactsReply, bridge.Result, error)
}

// publishColony publishes every colony facts sub-section
// (bridge.SplitColonyFacts, keyed by row path) of the review frame's colony
// facts (planning, no extra definitions) and returns the tables' mirror
// versions.
func publishColony(m *mirror.Mirror, scope mirror.Scope, observed *o.ColonyFactsSnapshot) map[string]uint64 {
	split := bridge.SplitColonyFacts(observed)
	tick := observed.GetContext().GetTick()
	versions := make(map[string]uint64, len(split))
	for _, name := range bridge.ColonySections() {
		versions[name] = mirror.Put(m, scope, name, split[name], mirror.At(tick)).Version
	}
	return versions
}

// colonyTables rebuilds the colony facts the mirror holds at versions,
// false when any section moved on or is gone.
func colonyTables(m *mirror.Mirror, scope mirror.Scope, versions map[string]uint64) (*o.ColonyFactsSnapshot, bool) {
	tables := make(map[string]map[string]bridge.ColonyRow, len(versions))
	for name, version := range versions {
		table, ok := mirror.Get[string, bridge.ColonyRow](m, scope, name)
		if !ok || table.Version != version {
			return nil, false
		}
		tables[name] = table.Rows
	}
	joined, err := bridge.JoinColonyFacts(tables)
	return joined, err == nil
}

// colonyFacts is a planner's colony facts read (no extra definitions):
// the review's mirrored census while it serves the planner's current
// identity under the part-1 reuse rule (sameObservedIdentity: the same
// world and generation, within the pawn cadence after the review, no clock
// invalidation since), otherwise native. A read without planning gets the
// planning section the native answers it with.
func (r *RoutineReviewer) colonyFacts(ctx context.Context, native colonyFactsReader, identity *c.Identity, planning bool) (*o.ColonyFactsReply, bridge.Result, error) {
	if r == nil || !sameNativeSource(native, r.native) {
		return native.ReadColonyFacts(ctx, identity, planning, nil)
	}
	return r.servedColonyFacts(ctx, native, identity, planning)
}

func (r *RoutineReviewer) servedColonyFacts(ctx context.Context, native colonyFactsReader, identity *c.Identity, planning bool) (*o.ColonyFactsReply, bridge.Result, error) {
	if v, ok := r.mirroredColony(ctx, identity); ok {
		if !planning {
			v.Planning = notRequestedPlanning()
		}
		return &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: v}}, bridge.Result{}, nil
	}
	return native.ReadColonyFacts(ctx, identity, planning, nil)
}

func (r *RoutineReviewer) mirroredColony(ctx context.Context, identity *c.Identity) (*o.ColonyFactsSnapshot, bool) {
	if r == nil || r.mirror == nil {
		return nil, false
	}
	r.census.mu.Lock()
	census, generation := r.census.latest, r.census.generation
	r.census.mu.Unlock()
	if census == nil || census.colony == nil || census.generation != generation {
		return nil, false
	}
	expected, err := routineScope(ctx, r.native)
	if err != nil || string(expected.Colony) != identity.GetColonyId() || string(expected.Load) != identity.GetLoadToken() || int32(expected.Map) != identity.GetMapId() {
		return nil, false
	}
	if !sameObservedIdentity(census.reading.Projection.Identity, expected) {
		return nil, false
	}
	generationValue, _ := expected.NativeGeneration.Value()
	scope := mirror.Scope{Load: string(expected.Load), Map: int32(expected.Map), Generation: uint64(generationValue)}
	return colonyTables(r.mirror, scope, census.colony)
}

// notRequestedPlanning is the planning section of a colony facts read
// that did not ask for planning, as the native answers it.
func notRequestedPlanning() *o.PlanningSection {
	return &o.PlanningSection{Outcome: &o.PlanningSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum(), Detail: proto.String("Planning was not requested.")}}}
}

// ColonyFacts serves the session's colony facts reads outside the routine
// planners (chat, caravan departure, deconstruction) from the review's
// mirrored census under the same reuse rule once a routine reviewer is
// bound (NewClockScheduler); unbound, every read is native.
type ColonyFacts struct {
	mu       sync.Mutex
	reviewer *RoutineReviewer
}

func (f *ColonyFacts) bind(r *RoutineReviewer) {
	if f == nil {
		return
	}
	f.mu.Lock()
	f.reviewer = r
	f.mu.Unlock()
}

func (f *ColonyFacts) read(ctx context.Context, native colonyFactsReader, identity *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, bridge.Result, error) {
	var r *RoutineReviewer
	if f != nil {
		f.mu.Lock()
		r = f.reviewer
		f.mu.Unlock()
	}
	if r == nil || len(definitions) > 0 {
		return native.ReadColonyFacts(ctx, identity, planning, definitions)
	}
	return r.servedColonyFacts(ctx, native, identity, planning)
}

// Chat is native with its colony facts read served by f.
func (f *ColonyFacts) Chat(native ChatFactsNative) ChatFactsNative {
	if native == nil {
		return nil
	}
	return chatColonyFacts{native, f}
}

type chatColonyFacts struct {
	ChatFactsNative
	facts *ColonyFacts
}

func (n chatColonyFacts) ReadColonyFacts(ctx context.Context, identity *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, bridge.Result, error) {
	return n.facts.read(ctx, n.ChatFactsNative, identity, planning, definitions)
}

type deconstructionColonyFacts struct {
	DeconstructionNative
	facts *ColonyFacts
}

func (n deconstructionColonyFacts) ReadColonyFacts(ctx context.Context, identity *c.Identity, planning bool, definitions []string) (*o.ColonyFactsReply, bridge.Result, error) {
	return n.facts.read(ctx, n.DeconstructionNative, identity, planning, definitions)
}
