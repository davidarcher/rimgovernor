package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func TestSleepingProjectionMapsRoomsPartnersAndTitle(t *testing.T) {
	u := &o.UpkeepFacts{
		People: []*o.UpkeepPerson{{Pawn: &o.PawnState{Pawn: &o.EntityRef{Id: proto.String("pawn")}}, PartnerIds: []string{"lover"}, BedSharingAllowed: proto.Bool(false),
			Title: &o.RoyalTitleFacts{DefName: proto.String("Knight"), Seniority: proto.Int32(100), BedroomMinArea: proto.Int32(24), BedroomMinImpressiveness: proto.Int32(40), BedroomFloored: proto.Bool(true), BedroomThings: []*o.BedroomThingRequirement{{AnyOf: []string{"DoubleBed", "RoyalBed"}, Count: proto.Int32(1)}}}}},
		Beds: []*o.UpkeepBed{{Bed: &o.EntityRef{Id: proto.String("bed"), DefName: proto.String("Bed")}, RoomId: proto.String("7"), Quality: proto.String("Good")}},
		Rooms: &o.UpkeepRoomsSection{Outcome: &o.UpkeepRoomsSection_Observed{Observed: &o.UpkeepRoomsFacts{Rooms: []*o.UpkeepRoom{
			{RoomId: proto.String("7"), Role: proto.String("Bedroom"), Space: proto.Float64(20), Beauty: proto.Float64(1), Cleanliness: proto.Float64(0), Wealth: proto.Float64(900), Impressiveness: proto.Float64(35), CellCount: proto.Uint32(16), BedIds: []string{"bed"}}}}}},
	}
	v := &o.ColonyFactsSnapshot{ColonistCount: proto.Uint32(1), Upkeep: &o.UpkeepSection{Outcome: &o.UpkeepSection_Observed{Observed: u}}}
	r, known := colonySleeping(v).Value()
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
	rooms, k := r.Rooms.Value()
	if !k || len(rooms) != 1 || rooms[0].ID != "7" || rooms[0].Role != "Bedroom" || len(rooms[0].Beds) != 1 {
		t.Fatal(rooms, k)
	}
	if q, k := rooms[0].Quality.Value(); !k || q.Impressiveness != 35 || q.Wealth != 900 {
		t.Fatal("quality", q)
	}
	if cells, k := rooms[0].Cells.Value(); !k || cells != 16 {
		t.Fatal("cells", cells)
	}

	u.Rooms.GetObserved().Rooms[0].Wealth = nil
	r, _ = colonySleeping(v).Value()
	if rooms, _ := r.Rooms.Value(); func() bool { _, k := rooms[0].Quality.Value(); return k }() {
		t.Fatal("partial quality became known")
	}

	// No title stays nil; a rooms issue leaves the census unknown while the
	// people and beds stay known.
	u.People[0].Title = nil
	u.Rooms = nil
	u.Issues = []*o.ReadIssue{{Field: proto.String("rooms")}}
	r, known = colonySleeping(v).Value()
	if !known || r.People[0].Title != nil {
		t.Fatal("title", r.People[0].Title)
	}
	if _, k := r.Rooms.Value(); k {
		t.Fatal("unavailable rooms became known")
	}
}
