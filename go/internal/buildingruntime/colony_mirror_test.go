package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/mirror"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	l "github.com/davidarcher/RimGovernor/go/internal/wire/lifecyclepb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// colonyCountingNative answers colony facts at tick and counts the reads.
type colonyCountingNative struct {
	observation.RoutineSource
	tick  int64
	facts *o.ColonyFactsSnapshot
	reads int
}

func (n *colonyCountingNative) context(id *c.Identity) *c.ObservationContext {
	return &c.ObservationContext{Identity: id, Tick: proto.Int64(n.tick), NativeGeneration: proto.Uint64(3)}
}

func (n *colonyCountingNative) Identity(context.Context) (*l.IdentityReply, bridge.Result, error) {
	id := &c.Identity{ColonyId: proto.String("c"), LoadToken: proto.String("l"), MapId: proto.Int32(1)}
	return &l.IdentityReply{Outcome: &l.IdentityReply_Loaded{Loaded: &l.LoadedIdentity{Context: n.context(id)}}}, bridge.Result{}, nil
}

func (n *colonyCountingNative) ReadColonyFacts(_ context.Context, id *c.Identity, planning bool) (*o.ColonyFactsReply, bridge.Result, error) {
	n.reads++
	v := proto.Clone(n.facts).(*o.ColonyFactsSnapshot)
	v.Context = n.context(id)
	if !planning {
		v.Planning = bridge.NotRequestedPlanning()
	}
	return &o.ColonyFactsReply{Outcome: &o.ColonyFactsReply_Observed{Observed: v}}, bridge.Result{}, nil
}

// TestColonySectionsServePlannersOfTheCensus: the review's colony facts
// read publishes one mirror section per sub-section and answers the
// snapshot the native sent; a planner of the same census, at a later tick
// of a running clock, is served from the mirror (a non-planning read gets
// the native's unrequested planning section) and reads natively once the
// census is invalidated.
func TestColonySectionsServePlannersOfTheCensus(t *testing.T) {
	t.Parallel()
	ctx := context.Background()
	facts := &o.ColonyFactsSnapshot{
		ColonistCount: proto.Uint32(3), Biome: proto.String("TemperateForest"),
		Resources: []*o.Quantity{{DefName: proto.String("Steel"), Units: proto.Int64(40)}, {DefName: proto.String("WoodLog"), Units: proto.Int64(7)}},
		Farms:     []*o.FarmFacts{{ZoneId: proto.String("Zone_1"), Crop: proto.String("Plant_Rice")}},
		Upkeep:    &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: &o.UpkeepFacts{}}},
		Planning:  &o.PlanningSection{Outcome: &o.PlanningSection_Observed{Observed: &o.PlanningFacts{}}},
	}
	native := &colonyCountingNative{tick: 100, facts: facts}
	r := &RoutineReviewer{mirror: mirror.New(), native: native}
	identity := observation.Identity{Colony: "c", Load: "l", Map: 1, Tick: 100, NativeGeneration: domain.Known(domain.NativeGeneration(3))}
	scope := mirror.Scope{Load: "l", Map: 1, Generation: 3}
	id := &c.Identity{ColonyId: proto.String("c"), LoadToken: proto.String("l"), MapId: proto.Int32(1)}

	published := proto.Clone(facts).(*o.ColonyFactsSnapshot)
	published.Context = native.context(id)
	versions := publishColony(r.mirror, scope, published)
	if table, ok := mirror.Get[string, bridge.ColonyRow](r.mirror, scope, "colony.resources"); !ok || len(table.Rows) != 3 {
		t.Fatalf("resources section = %+v", table)
	}
	r.census.rememberColony(versions)
	reading := observation.RoutineReading{}
	reading.Projection.Identity = identity
	r.census.retain(reading, false, domain.Unknown[[]policy.ConstructionClaim]())

	native.tick = 140
	served, _, err := r.colonyFacts(ctx, native, id, false)
	if err != nil || native.reads != 0 {
		t.Fatalf("planner read: %v reads=%d", err, native.reads)
	}
	if served.GetObserved().GetContext().GetTick() != 100 || served.GetObserved().GetPlanning().GetUnavailable() == nil || len(served.GetObserved().GetResources()) != 2 {
		t.Fatalf("planner served %v", served.GetObserved())
	}
	r.census.invalidate()
	if _, _, err := r.colonyFacts(ctx, native, id, false); err != nil || native.reads != 1 {
		t.Fatalf("invalidated census served colony facts: reads=%d err=%v", native.reads, err)
	}
}
