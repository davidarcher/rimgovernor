package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
	"testing"
)

func TestUpkeepProjectionPreservesSectionsAndFalsePresence(t *testing.T) {
	u := &o.UpkeepFacts{Fires: []*o.FireState{{Fire: &o.EntityRef{Id: proto.String("fire")}, Home: proto.Bool(true)}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	r, err := policy.ReviewUpkeep(colonyUpkeep(v), policy.UpkeepHistory{}, nil)
	if err != nil || !r.Needs[0].Active || !r.Needs[0].Unsafe || r.Needs[1].Active {
		t.Fatal(r, err)
	}
	u.Fires[0].Home = proto.Bool(false)
	r, err = policy.ReviewUpkeep(colonyUpkeep(v), r.History, nil)
	if err != nil || r.Needs[0].Active {
		t.Fatal("false Home treated as absent", r, err)
	}
	u.Fires[0].Home = nil
	f := colonyUpkeep(v)
	if _, known := f.Fires.Value(); known {
		t.Fatal("missing Home became known")
	}
	u.Fires = nil
	u.Issues = []*o.ReadIssue{{Field: proto.String("fires")}}
	f = colonyUpkeep(v)
	if _, known := f.Fires.Value(); known {
		t.Fatal("failed empty census recovered")
	}
	if _, known := f.Items.Value(); !known {
		t.Fatal("unrelated census lost")
	}
	u.Items = []*o.UpkeepItem{{}}
	if _, known := colonyUpkeep(v).Items.Value(); known {
		t.Fatal("partial item became known")
	}
	if _, known := colonyUpkeep(&o.ColonyFactsSnapshot{}).Items.Value(); known {
		t.Fatal("absent section became empty")
	}
}

func TestUpkeepFilthCarriesRoomIdentity(t *testing.T) {
	u := &o.UpkeepFacts{Filth: []*o.FilthState{
		{Filth: &o.EntityRef{Id: proto.String("blood"), DefName: proto.String("Filth_Blood")}, Home: proto.Bool(true), Thickness: proto.Uint32(2), RoomRole: proto.String("Kitchen"), RoomId: proto.String("7")},
		{Filth: &o.EntityRef{Id: proto.String("dirt")}, Home: proto.Bool(true), Thickness: proto.Uint32(1)},
	}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	rows, known := colonyUpkeep(v).Filth.Value()
	if !known || len(rows) != 2 {
		t.Fatal(rows, known)
	}
	if id, ok := rows[0].RoomID.Value(); !ok || id != "7" || rows[0].Definition != "Filth_Blood" || rows[0].Room != "Kitchen" {
		t.Fatal(rows[0])
	}
	if _, ok := rows[1].RoomID.Value(); ok {
		t.Fatal("outdoor filth gained a room", rows[1])
	}
}

func TestUpkeepProjectionDecodesLighting(t *testing.T) {
	cell := func(x, z int32) *c.Cell { return &c.Cell{X: proto.Int32(x), Z: proto.Int32(z)} }
	lighting := &o.LightingFacts{
		WorkCells: []*o.WorkLightCell{{Bench: &o.EntityRef{Id: proto.String("stove"), DefName: proto.String("FueledStove"), Position: cell(10, 10)}, Cell: cell(10, 11), Glow: proto.Float64(0.1), Roofed: proto.Bool(true), RoomId: proto.String("7")}},
		Lamps:     []*o.LampState{{Building: &o.BuildingState{Building: &o.EntityRef{Id: proto.String("lamp"), DefName: proto.String("StandingLamp"), Position: cell(12, 12)}, Service: &o.BuildingServiceState{Connected: proto.Bool(true), PowerOn: proto.Bool(false), SwitchedOn: proto.Bool(true), BrokenDown: proto.Bool(false)}}, GlowRadius: proto.Float64(12), Lit: proto.Bool(false), RoomId: proto.String("7")}},
	}
	u := &o.UpkeepFacts{Lighting: &o.LightingSection{Outcome: &o.LightingSection_Observed{Observed: lighting}}}
	v := &o.ColonyFactsSnapshot{Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	f, known := colonyUpkeep(v).Lighting.Value()
	if !known || len(f.WorkCells) != 1 || len(f.Lamps) != 1 {
		t.Fatal(f, known)
	}
	w := f.WorkCells[0]
	if w.Bench != "stove" || w.Definition != "FueledStove" || w.Cell != (domain.Cell{X: 10, Z: 11}) || w.Glow != 0.1 || !w.Roofed {
		t.Fatal(w)
	}
	if room, ok := w.Room.Value(); !ok || room != "7" {
		t.Fatal(w)
	}
	l := f.Lamps[0]
	if l.ID != "lamp" || l.Definition != "StandingLamp" || l.Cell != (domain.Cell{X: 12, Z: 12}) || l.Radius != 12 || l.Lit {
		t.Fatal(l)
	}
	if powered, ok := l.Powered.Value(); !ok || powered {
		t.Fatal(l)
	}
	if _, ok := l.OutOfFuel.Value(); ok {
		t.Fatal("fuel invented", l)
	}
	lighting.WorkCells[0].Glow = nil
	if _, known := colonyUpkeep(v).Lighting.Value(); known {
		t.Fatal("unmeasured glow became known")
	}
	lighting.WorkCells[0].Glow = proto.Float64(0.1)
	lighting.Lamps[0].Lit = nil
	if _, known := colonyUpkeep(v).Lighting.Value(); known {
		t.Fatal("unmeasured lamp became known")
	}
	u.Lighting = nil
	if _, known := colonyUpkeep(v).Lighting.Value(); known {
		t.Fatal("absent section became known")
	}
	u.Lighting = &o.LightingSection{Outcome: &o.LightingSection_Observed{Observed: &o.LightingFacts{}}}
	if f, known := colonyUpkeep(v).Lighting.Value(); !known || len(f.WorkCells) != 0 {
		t.Fatal("empty census is a known census", f, known)
	}
	u.Issues = []*o.ReadIssue{{Field: proto.String("lighting")}}
	if _, known := colonyUpkeep(v).Lighting.Value(); known {
		t.Fatal("failed section became known")
	}
}
