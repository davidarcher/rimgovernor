package bridge

import (
	"context"
	"math"
	"testing"
	"time"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

func TestAcquisitionCensusBindsSourceSnapshotAndYield(t *testing.T) {
	base := colonyFixture(t).GetObserved()
	base.Acquisition = []*o.AcquisitionFacts{{Source: &o.EntityRef{Id: proto.String("plant"), DefName: proto.String("Oak"), MapId: base.Context.Identity.MapId, Position: proto.Clone(base.Center).(*c.Cell), Snapshot: &o.SnapshotRef{EntityId: proto.String("plant"), Token: proto.String("cas"), Context: proto.Clone(base.Context).(*c.ObservationContext)}}, Resource: proto.String("WoodLog"), Hunt: proto.Bool(false), Tree: proto.Bool(true), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(10), NutritionYield: proto.Float64(0)}}
	base.Issues = nil
	if err := validateColonyAcquisition(base); err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*o.ColonyFactsSnapshot){
		func(v *o.ColonyFactsSnapshot) {
			v.Acquisition[0].Source.Snapshot.Context.Tick = proto.Int64(v.Context.GetTick() + 1)
		},
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Source.Snapshot.EntityId = proto.String("other") },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Yield = proto.Float64(math.NaN()) },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].Food = nil },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition = append(v.Acquisition, v.Acquisition[0]) },
		func(v *o.ColonyFactsSnapshot) { v.Acquisition[0].NutritionYield = proto.Float64(1) },
		func(v *o.ColonyFactsSnapshot) { v.Issues = []*o.ReadIssue{{Field: proto.String("acquisition")}} },
		// An inedible hunt of anything but a recognised pest.
		func(v *o.ColonyFactsSnapshot) {
			v.Acquisition[0].Hunt, v.Acquisition[0].Tree, v.Acquisition[0].Yield = proto.Bool(true), proto.Bool(false), proto.Float64(1)
			v.Acquisition[0].Source.DefName = proto.String("Muffalo")
		},
	} {
		v := proto.Clone(base).(*o.ColonyFactsSnapshot)
		change(v)
		if validateColonyAcquisition(v) == nil {
			t.Fatal("malformed acquisition accepted", v)
		}
	}
}

func TestAcquisitionCensusAcceptsAnInediblePestHunt(t *testing.T) {
	base := colonyFixture(t).GetObserved()
	base.Acquisition = []*o.AcquisitionFacts{{Source: &o.EntityRef{Id: proto.String("beaver"), DefName: proto.String("Alphabeaver"), MapId: base.Context.Identity.MapId, Position: proto.Clone(base.Center).(*c.Cell), Snapshot: &o.SnapshotRef{EntityId: proto.String("beaver"), Token: proto.String("cas"), Context: proto.Clone(base.Context).(*c.ObservationContext)}}, RevengeChance: proto.Float64(0.1), HerdSize: proto.Uint32(3), MeleeOnly: proto.Bool(false), Downed: proto.Bool(false), WeaponRange: proto.Float64(30), Resource: proto.String("Corpse_Alphabeaver"), Hunt: proto.Bool(true), Tree: proto.Bool(false), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(1), NutritionYield: proto.Float64(0)}}
	base.Issues = nil
	if err := validateColonyAcquisition(base); err != nil {
		t.Fatal(err)
	}
}

func TestHuntCostsRequireCompleteBoundedFacts(t *testing.T) {
	base := colonyFixture(t).GetObserved()
	source := &o.EntityRef{Id: proto.String("deer"), DefName: proto.String("Deer"), MapId: base.Context.Identity.MapId, Position: proto.Clone(base.Center).(*c.Cell), Snapshot: &o.SnapshotRef{EntityId: proto.String("deer"), Token: proto.String("cas"), Context: proto.Clone(base.Context).(*c.ObservationContext)}}
	base.Acquisition = []*o.AcquisitionFacts{{Source: source, Resource: proto.String("Corpse_Deer"), Hunt: proto.Bool(true), Tree: proto.Bool(false), Food: proto.Bool(true), Designated: proto.Bool(false), Yield: proto.Float64(1), NutritionYield: proto.Float64(10), RevengeChance: proto.Float64(0), HerdSize: proto.Uint32(1), MeleeOnly: proto.Bool(true), Downed: proto.Bool(false), WeaponRange: proto.Float64(0)}}
	base.Issues = nil
	if err := validateColonyAcquisition(base); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.AcquisitionFacts){
		func(r *o.AcquisitionFacts) { r.RevengeChance = nil },
		func(r *o.AcquisitionFacts) { r.RevengeChance = proto.Float64(math.NaN()) },
		func(r *o.AcquisitionFacts) { r.RevengeChance = proto.Float64(1.1) },
		func(r *o.AcquisitionFacts) { r.HerdSize = proto.Uint32(0) },
		func(r *o.AcquisitionFacts) { r.MeleeOnly = nil },
		func(r *o.AcquisitionFacts) { r.Downed = nil },
		func(r *o.AcquisitionFacts) { r.WeaponRange = proto.Float64(-1) },
	} {
		v := proto.Clone(base).(*o.ColonyFactsSnapshot)
		mutate(v.Acquisition[0])
		if validateColonyAcquisition(v) == nil {
			t.Fatal("accepted invalid hunt cost", v.Acquisition[0])
		}
	}
}

// A hunt follows its animal (#321): the read matches a hunt row by the
// animal wherever the census reports it, keeping the action's cell as the
// hint; a plant row is still matched by cell.
func TestReadAcquisitionFollowsAHuntByAnimal(t *testing.T) {
	r := colonyFixture(t)
	v := r.GetObserved()
	center := v.Center
	away := &c.Cell{X: proto.Int32(center.GetX() + 9), Z: proto.Int32(center.GetZ())}
	v.Acquisition = []*o.AcquisitionFacts{
		{Source: &o.EntityRef{Id: proto.String("beaver"), DefName: proto.String("Alphabeaver"), MapId: v.Context.Identity.MapId, Position: away, Snapshot: &o.SnapshotRef{EntityId: proto.String("beaver"), Token: proto.String("cas"), Context: proto.Clone(v.Context).(*c.ObservationContext)}}, RevengeChance: proto.Float64(0.1), HerdSize: proto.Uint32(3), MeleeOnly: proto.Bool(false), Downed: proto.Bool(false), WeaponRange: proto.Float64(30), Resource: proto.String("Corpse_Alphabeaver"), Hunt: proto.Bool(true), Tree: proto.Bool(false), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(1), NutritionYield: proto.Float64(0)},
		{Source: &o.EntityRef{Id: proto.String("plant"), DefName: proto.String("Oak"), MapId: v.Context.Identity.MapId, Position: proto.Clone(away).(*c.Cell), Snapshot: &o.SnapshotRef{EntityId: proto.String("plant"), Token: proto.String("cas"), Context: proto.Clone(v.Context).(*c.ObservationContext)}}, Resource: proto.String("WoodLog"), Hunt: proto.Bool(false), Tree: proto.Bool(true), Food: proto.Bool(false), Designated: proto.Bool(false), Yield: proto.Float64(10), NutritionYield: proto.Float64(0)},
	}
	v.Issues = nil
	v.Planning = &o.PlanningSection{Outcome: &o.PlanningSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_NOT_REQUESTED.Enum()}}}
	id := proto.Clone(v.Context.Identity).(*c.Identity)
	client := testClient(t, &testServer{schema: protoSchema, handler: func(_ context.Context, arg nativeArgument) (*mcp.CallToolResult, error) {
		return pbResult(r), nil
	}}, time.Second)
	planned := domain.Cell{X: center.GetX(), Z: center.GetZ()}
	hunt, err := domain.NewAcquisition("beaver", "Corpse_Alphabeaver", planned)
	if err != nil {
		t.Fatal(err)
	}
	read, _, err := client.ReadAcquisition(context.Background(), id, hunt)
	if err != nil || len(read.Targets) != 1 || read.Targets[0].Acquisition != hunt || read.Targets[0].Token != "cas" {
		t.Fatal(read, err)
	}
	// The plant at the planned cell is what a plant action reads there;
	// nothing at a cell nobody stands on.
	harvest, err := domain.NewAcquisition("plant", "WoodLog", planned)
	if err != nil {
		t.Fatal(err)
	}
	if read, _, err = client.ReadAcquisition(context.Background(), id, harvest); err != nil || len(read.Targets) != 0 {
		t.Fatal("a plant is matched by cell, not name", read, err)
	}
	harvest, _ = domain.NewAcquisition("plant", "WoodLog", domain.Cell{X: away.GetX(), Z: away.GetZ()})
	if read, _, err = client.ReadAcquisition(context.Background(), id, harvest); err != nil || len(read.Targets) != 1 || read.Targets[0].Acquisition != harvest {
		t.Fatal(read, err)
	}
}
