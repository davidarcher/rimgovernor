package observation

import (
	"github.com/davidarcher/RimGovernor/go/internal/policy"
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
