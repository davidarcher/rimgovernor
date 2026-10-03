package observation

import (
	"os"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/encoding/protojson"
	"google.golang.org/protobuf/proto"
)

func cell(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }

// A recorded Biotech colony read projects every row, keeps absent scalars
// unknown, and a section that is absent or unavailable stays unknown.
func TestBiotechColonyProjection(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/fixtures/colony-core.json")
	if err != nil {
		t.Fatal(err)
	}
	r := &o.ColonyFactsReply{}
	if err = protojson.Unmarshal(data, r); err != nil {
		t.Fatal(err)
	}
	id := Identity{Colony: "colony", Load: "load", Map: 0, Tick: 7, NativeGeneration: domain.Known(domain.NativeGeneration(1))}
	decode := func() ColonyProjection {
		t.Helper()
		p, err := DecodeColony(r, id, bridge.Tables{})
		if err != nil {
			t.Fatal(err)
		}
		return p
	}
	for _, section := range []*o.BiotechSection{nil, {Outcome: &o.BiotechSection_Unavailable{Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum(), Detail: proto.String("unreadable")}}}} {
		r.GetObserved().Biotech = section
		if _, known := decode().Biotech.Value(); known {
			t.Fatal("absent or unavailable section became known")
		}
	}
	f := &o.BiotechColonyFacts{
		Pollution:  &o.PollutionTotals{TotalPollution: proto.Int32(12), PollutableCells: proto.Uint32(400), ClearAreaId: proto.String("Area_PollutionClear"), ClearAreaCells: proto.Int32(30)},
		Polluters:  []*o.Polluter{{ThingId: proto.String("toxifier"), DefName: proto.String("ToxifierGenerator"), Position: cell(10, 10), Polluting: proto.Bool(true)}},
		Wastepacks: []*o.Wastepack{{ThingId: proto.String("pack"), DefName: proto.String("Wastepack"), Position: cell(11, 10), Count: proto.Int32(5), Frozen: proto.Bool(false), Outdoors: proto.Bool(true)}},
		Atomizers:  []*o.WastepackAtomizer{{ThingId: proto.String("atomizer"), DefName: proto.String("WastepackAtomizer"), Position: cell(12, 10), Powered: proto.Bool(true), FillPercent: proto.Float64(0.5)}},
		Pumps:      []*o.PollutionPump{{ThingId: proto.String("pump"), DefName: proto.String("PollutionPump"), Position: cell(13, 10), DisabledByArtificialBuildings: proto.Bool(true)}},
		Gestators: []*o.MechGestatorState{
			{ThingId: proto.String("gestator"), DefName: proto.String("SubcoreScanner"), Position: cell(14, 10), Powered: proto.Bool(true), BillId: proto.String("bill"), State: proto.String("Forming"), MechKind: proto.String("Mech_Lifter"), BoundPawnId: proto.String("mech"), CyclesCompleted: proto.Int32(1), FormingPercent: proto.Float64(0.25), WasteCount: proto.Int32(3)},
			{ThingId: proto.String("idle"), DefName: proto.String("SubcoreScanner"), Position: cell(15, 10)}},
		Chargers: []*o.MechChargerState{{ThingId: proto.String("charger"), DefName: proto.String("MechCharger"), Position: cell(16, 10), FullOfWaste: proto.Bool(true), WasteCount: proto.Int32(4), ChargingMechId: proto.String("mech")}},
		Babies: []*o.BabyCare{{PawnId: proto.String("baby"), WantsSuckle: proto.Bool(true), InBed: proto.Bool(false),
			Autofeeders: []*o.BabyAutofeeder{{PawnId: proto.String("mum"), Mode: proto.String("Childcare")}}}},
		Breastfeeders: []string{"mum"},
	}
	r.GetObserved().Biotech = &o.BiotechSection{Outcome: &o.BiotechSection_Observed{Observed: f}}
	v, known := decode().Biotech.Value()
	if !known {
		t.Fatal("observed section unknown")
	}
	if n, ok := v.TotalPollution.Value(); !ok || n != 12 || len(v.Polluters) != 1 || len(v.Wastepacks) != 1 || len(v.Atomizers) != 1 || len(v.Pumps) != 1 || len(v.Gestators) != 2 || len(v.Chargers) != 1 || len(v.Babies) != 1 {
		t.Fatalf("lost rows: %+v", v)
	}
	if d, ok := v.Pumps[0].DisabledByArtificialBuildings.Value(); !ok || !d {
		t.Fatal("pump disabled flag lost")
	}
	if _, ok := v.Pumps[0].Powered.Value(); ok {
		t.Fatal("absent powered became known")
	}
	if g := v.Gestators[0]; g.MechKind != domain.Known("Mech_Lifter") || g.WasteCount != domain.Known(int32(3)) {
		t.Fatalf("gestator %+v", g)
	}
	if _, ok := v.Gestators[1].BillID.Value(); ok {
		t.Fatal("billless gestator reports a bill")
	}
	if b := v.Babies[0]; len(b.Autofeeders) != 1 || b.Autofeeders[0].Mode != "Childcare" || len(v.Breastfeeders) != 1 {
		t.Fatalf("baby %+v", b)
	}
	// Contract violations are refused, never projected.
	for name, change := range map[string]func(){
		"duplicate gestator":     func() { f.Gestators[1].ThingId = proto.String("gestator") },
		"off map charger":        func() { f.Chargers[0].Position = cell(-1, 0) },
		"negative waste":         func() { f.Chargers[0].WasteCount = proto.Int32(-1) },
		"empty wastepack":        func() { f.Wastepacks[0].Count = proto.Int32(0) },
		"bill fields sans bill":  func() { f.Gestators[1].State = proto.String("Forming") },
		"duplicate baby":         func() { f.Babies = append(f.Babies, f.Babies[0]) },
		"duplicate breastfeeder": func() { f.Breastfeeders = []string{"mum", "mum"} },
	} {
		saved := proto.Clone(f).(*o.BiotechColonyFacts)
		change()
		if _, err := DecodeColony(r, id, bridge.Tables{}); err == nil {
			t.Fatalf("%s accepted", name)
		}
		proto.Reset(f)
		proto.Merge(f, saved)
	}
}
