package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestSleepingProjectionMapsRoomsPartnersAndTitle(t *testing.T) {
	u := &o.UpkeepFacts{
		People: []*o.UpkeepPerson{{Pawn: &commonpb.Ref{Id: proto.String("pawn")}, Partners: bridge.NewRefs([]string{"lover"}), BedSharingAllowed: proto.Bool(false),
			Title: &o.RoyalTitleFacts{DefName: proto.String("Knight"), Seniority: proto.Int32(100), BedroomMinArea: proto.Int32(24), BedroomMinImpressiveness: proto.Int32(40), BedroomFloored: proto.Bool(true), BedroomThings: []*o.BedroomThingRequirement{{AnyOf: []string{"DoubleBed", "RoyalBed"}, Count: proto.Int32(1)}}}}},
		Beds: []*o.UpkeepBed{{Bed: &commonpb.Ref{Id: proto.String("bed")}, Room: &commonpb.Ref{Id: proto.String("7")}, Quality: proto.String("Good"), Humanlike: proto.Bool(true), Medical: proto.Bool(false), Prisoners: proto.Bool(false)}},
	}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1), Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	if _, k := colonySleeping(v, nil).Value(); k {
		t.Fatal("an unresolved bed became a known census")
	}
	r, known := colonySleeping(v, buildingRows(&o.BuildingState{Building: &o.EntityRef{Id: proto.String("bed"), DefName: proto.String("Bed")}})).Value()
	if !known {
		t.Fatal("unknown")
	}
	p := r.People[0]
	if len(p.Partners) != 1 || p.Partners[0] != "lover" {
		t.Fatal(p.Partners)
	}
	if share, k := p.BedSharingAllowed.Value(); !k || share {
		t.Fatal("bed sharing", share, k)
	}
	if p.Title == nil || p.Title.Definition != "Knight" || p.Title.Seniority != 100 || p.Title.BedroomMinArea != 24 || p.Title.BedroomMinImpressiveness != 40 || !p.Title.BedroomFloored ||
		len(p.Title.BedroomThings) != 1 || p.Title.BedroomThings[0].Count != 1 || p.Title.BedroomThings[0].AnyOf[1] != policy.Resource("RoyalBed") {
		t.Fatal(p.Title)
	}
	if room, k := r.Beds[0].Room.Value(); !k || room != "7" {
		t.Fatal("bed room", room)
	}
	if q, k := r.Beds[0].Quality.Value(); !k || q != "Good" {
		t.Fatal("bed quality", q)
	}
	stat := func(name string, value float64) *o.RoomStat {
		return &o.RoomStat{DefName: proto.String(name), Value: proto.Float64(value)}
	}
	stats := func() []*o.RoomStat {
		return []*o.RoomStat{stat("Space", 20), stat("Beauty", 1), stat("Cleanliness", 0), stat("Wealth", 900), stat("Impressiveness", 35)}
	}
	census := &o.RoomsSnapshot{Rooms: []*o.RoomState{
		{Id: proto.String("7"), Role: proto.String("Bedroom"), CellCount: proto.Uint32(16), Stats: stats()},
		{Id: proto.String("8"), Role: proto.String("DiningRoom"), CellCount: proto.Uint32(30), Stats: stats()},
		{Id: proto.String("9"), Role: proto.String("Workshop"), CellCount: proto.Uint32(30), Stats: stats()},
		{Id: proto.String("10"), Role: proto.String("RecRoom"), Fogged: proto.Bool(true), CellCount: proto.Uint32(30), Stats: stats()},
	}}
	rooms, k := upkeepRooms(census, r.Beds).Value()
	if !k || len(rooms) != 2 || rooms[1].ID != "8" || len(rooms[1].Beds) != 0 || rooms[0].ID != "7" || rooms[0].Role != "Bedroom" || len(rooms[0].Beds) != 1 {
		t.Fatal(rooms, k)
	}
	if q, k := rooms[0].Quality.Value(); !k || q.Impressiveness != 35 || q.Wealth != 900 {
		t.Fatal("quality", q)
	}
	if cells, k := rooms[0].Cells.Value(); !k || cells != 16 {
		t.Fatal("cells", cells)
	}

	census.Rooms[0].Stats[3].Value = nil
	if rooms, _ := upkeepRooms(census, r.Beds).Value(); func() bool { _, k := rooms[0].Quality.Value(); return k }() {
		t.Fatal("partial quality became known")
	}

	// No title stays nil; the colony read alone leaves room quality unknown
	// (the frame's rooms census fills it), and so does a bed of unknown kind.
	u.People[0].Title = nil
	r, known = colonySleeping(v, buildingRows(&o.BuildingState{Building: &o.EntityRef{Id: proto.String("bed"), DefName: proto.String("Bed")}})).Value()
	if !known || r.People[0].Title != nil {
		t.Fatal("title", r.People[0].Title)
	}
	if _, k := r.Rooms.Value(); k {
		t.Fatal("rooms known without the census")
	}
	r.Beds[0].Medical = domain.Unknown[bool]()
	if _, k := upkeepRooms(census, r.Beds).Value(); k {
		t.Fatal("rooms known with a bed of unknown kind")
	}
}
