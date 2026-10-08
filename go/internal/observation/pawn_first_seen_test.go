package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func visitor(id, faction string) *o.PawnState {
	return &o.PawnState{Pawn: &o.EntityRef{Id: proto.String(id)}, Humanlike: proto.Bool(true),
		Standing: &o.PawnStanding{FactionDefName: proto.String(faction), RoyalTitle: proto.String("Knight"),
			HostFaction: &c.Ref{Id: proto.String("Faction_1")}, GuestStatus: proto.String("Guest"), QuestLodger: proto.Bool(true)}}
}

func TestFirstSeenRecordsOnceAndResetsWithTheWorld(t *testing.T) {
	var r FirstSeenRecord
	world := Identity{Colony: "col", Map: 0, Load: "load-1", Tick: 100}
	r.Observe(world, bridge.NewPawns(visitor("Thing_Human1", "OutlanderCivil")))
	got, ok := r.Get(world, "Thing_Human1")
	want := PawnFirstSeen{Tick: 100, Faction: "OutlanderCivil", Title: "Knight", HostFaction: "Faction_1", Guest: "Guest", QuestLodger: true}
	if !ok || got != want {
		t.Fatalf("first seen %+v %v", got, ok)
	}
	// A refresh with changed facts later keeps the first sighting.
	later := world
	later.Tick = 500
	r.Observe(later, bridge.NewPawns(visitor("Thing_Human1", "Pirate"), visitor("Thing_Human2", "Pirate")))
	if got, _ := r.Get(world, "Thing_Human1"); got != want {
		t.Fatalf("refresh changed first seen: %+v", got)
	}
	if got, ok := r.Get(world, "Thing_Human2"); !ok || got.Tick != 500 || got.Faction != "Pirate" {
		t.Fatalf("new pawn %+v %v", got, ok)
	}
	// Animals and rows without standing are not recorded.
	r.Observe(later, bridge.NewPawns(&o.PawnState{Pawn: &o.EntityRef{Id: proto.String("Thing_Dog")}, Animal: proto.Bool(true)}))
	if _, ok := r.Get(later, "Thing_Dog"); ok {
		t.Fatal("animal recorded")
	}
	// A new world resets the record; the old world sees nothing either.
	reloaded := Identity{Colony: "col", Map: 0, Load: "load-2", Tick: 900}
	r.Observe(reloaded, bridge.NewPawns(visitor("Thing_Human1", "Pirate")))
	if got, _ := r.Get(reloaded, "Thing_Human1"); got.Tick != 900 || got.Faction != "Pirate" {
		t.Fatalf("reload first seen %+v", got)
	}
	if _, ok := r.Get(world, "Thing_Human2"); ok {
		t.Fatal("old world survived reset")
	}
}
