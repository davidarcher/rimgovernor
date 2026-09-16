package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func TestColonyEnvironmentDecodesLampsGrowersRoomsAndNetworks(t *testing.T) {
	env := &o.ControlledEnvironment{OutdoorTemperatureC: proto.Float64(-5), Daylight: proto.Bool(false),
		Lights:   []*o.GrowLight{{Building: &o.EntityRef{Id: proto.String("lamp-1"), DefName: proto.String("SunLamp"), Position: &c.Cell{X: proto.Int32(10), Z: proto.Int32(10)}}, RoomId: proto.String("7"), Powered: proto.Bool(true), PowerW: proto.Float64(2900), LitNow: proto.Bool(true), PowerNetId: proto.String("net-a"), GrowthCells: []*c.Cell{{X: proto.Int32(10), Z: proto.Int32(10)}, {X: proto.Int32(11), Z: proto.Int32(10)}}}},
		Growers:  []*o.PlantGrower{{Building: &o.EntityRef{Id: proto.String("basin-1"), DefName: proto.String("HydroponicsBasin"), Position: &c.Cell{X: proto.Int32(20), Z: proto.Int32(10)}}, Fertility: proto.Float64(2.8), SowTag: proto.String("Hydroponic"), CanSow: proto.Bool(true), PowerNetId: proto.String("net-a"), PlantCells: []*c.Cell{{X: proto.Int32(20), Z: proto.Int32(10)}, {X: proto.Int32(20), Z: proto.Int32(11)}, {X: proto.Int32(20), Z: proto.Int32(12)}, {X: proto.Int32(20), Z: proto.Int32(13)}}}},
		Rooms:    []*o.GrowRoom{{RoomId: proto.String("7"), TemperatureC: proto.Float64(21), CellCount: proto.Uint32(36), OpenRoofCount: proto.Uint32(0), ProperRoom: proto.Bool(true), PsychologicallyOutdoors: proto.Bool(false), LitCells: proto.Uint32(30)}},
		Networks: []*o.PowerHeadroom{{Id: proto.String("net-a"), GenerationW: proto.Float64(3000), SolarW: proto.Float64(1700), WindW: proto.Float64(300), ConsumptionW: proto.Float64(600), StoredWattDays: proto.Float64(400), CapacityWattDays: proto.Float64(600), HasActiveSource: proto.Bool(true)}}}
	env.Completeness = &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(4), Returned: proto.Uint64(4), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	definitions := []*o.PlanningDefinition{
		{Definition: &o.DefinitionRef{DefName: proto.String("Plant_Rice")}, GrowDays: proto.Float64(3), GrowMinGlow: proto.Float64(0.3), SowTags: []string{"Ground", "Hydroponic"}},
		{Definition: &o.DefinitionRef{DefName: proto.String("SunLamp")}, PowerW: proto.Float64(2900), GlowRadius: proto.Float64(14)},
		{Definition: &o.DefinitionRef{DefName: proto.String("HydroponicsBasin")}, PowerW: proto.Float64(70), GrowerFertility: proto.Float64(2.8), SowTag: proto.String("Hydroponic")},
	}
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	reply := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, reply); err != nil {
		t.Fatal(err)
	}
	planning := reply.GetObserved().Planning.GetObserved()
	planning.Environment = env
	planning.Definitions = definitions
	planning.Completeness = &o.Completeness{Page: &c.PageInfo{Complete: proto.Bool(true)}, Matched: proto.Uint64(3), Returned: proto.Uint64(3), Filtered: proto.Uint64(0), Unreadable: proto.Uint64(0)}
	if err = bridge.ValidateColonyFacts(reply.GetObserved(), reply.GetObserved().Context.Identity); err != nil {
		t.Fatal(err)
	}
	id, err := contextIdentity(reply.GetObserved().Context)
	if err != nil {
		t.Fatal(err)
	}
	p, err := DecodeColony(reply, id)
	if err != nil {
		t.Fatal(err)
	}
	e, known := p.Environment.Value()
	if !known || len(e.Lights) != 1 || len(e.Growers) != 1 || len(e.Rooms) != 1 || len(e.Networks) != 1 {
		t.Fatal(p.Environment)
	}
	if e.Lights[0].ID != "lamp-1" || e.Lights[0].Room != domain.Known("7") || len(e.Lights[0].GrowthCells) != 2 || e.LitCells()[domain.Cell{X: 11, Z: 10}] != "lamp-1" {
		t.Fatal(e.Lights[0])
	}
	if e.Growers[0].Fertility != domain.Known(2.8) || len(e.Growers[0].Cells) != 4 || e.Rooms[0].Lit != domain.Known(30) || e.Rooms[0].TemperatureC != domain.Known(21.0) {
		t.Fatal(e.Growers[0], e.Rooms[0])
	}
	net, ok := e.Network("net-a")
	if !ok || net.NightHeadroomW() != domain.Known(700.0) || net.CalmNightHeadroomW() != domain.Known(400.0) {
		t.Fatal(net)
	}
	var rice, lamp, basin PlanningDefinition
	for _, d := range p.Definitions {
		switch d.Name {
		case "Plant_Rice":
			rice = d
		case "SunLamp":
			lamp = d
		case "HydroponicsBasin":
			basin = d
		}
	}
	crop := policy.CropChoice{Name: rice.Name, SowTags: rice.SowTags, MinGlow: rice.GrowMinGlow}
	if !policy.GrowerAccepts(e.Growers[0], crop) || policy.GrowsInDark(crop) || lamp.PowerW != domain.Known(2900.0) || basin.SowTag != domain.Known("Hydroponic") || basin.GrowerFertility != domain.Known(2.8) {
		t.Fatal(rice, lamp, basin)
	}
	// A missing sow-tag list on a non-plant row stays unknown, and a section
	// issue withholds the whole environment.
	if _, known := lamp.SowTags.Value(); known {
		t.Fatal("building grew sow tags")
	}
	planning.Environment = nil
	planning.Issues = append(planning.Issues, &o.ReadIssue{Field: proto.String("environment"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}})
	p, err = DecodeColony(reply, id)
	if _, known := p.Environment.Value(); err != nil || known {
		t.Fatal("environment issue ignored", err)
	}
	planning.Issues, planning.Environment = nil, nil
	p, _ = DecodeColony(reply, id)
	if _, known := p.Environment.Value(); known {
		t.Fatal("absent environment became known")
	}
}
