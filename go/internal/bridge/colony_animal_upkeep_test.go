package bridge

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func animalWire() *o.UpkeepFacts {
	v := upkeepWire()
	v.Animals = []*o.AnimalFeed{{Pawn: &commonpb.Ref{Id: proto.String("animal")}, RequiresPen: proto.Bool(true)}}
	return v
}

// An upkeep animal is a reference into the pawn table; its herd
// facts are the table row's.
func TestAnimalUpkeepBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateDirectUpkeep(animalWire(), size, 3); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.UpkeepFacts){
		func(v *o.UpkeepFacts) { v.Animals[0].Pawn = nil },
		func(v *o.UpkeepFacts) { v.Animals = append(v.Animals, v.Animals[0]) },
		func(v *o.UpkeepFacts) { v.Issues = []*o.ReadIssue{{Field: proto.String("animals")}} },
		func(v *o.UpkeepFacts) {
			v.Animals[0].RequiresPen = proto.Bool(false)
			v.Animals[0].SuitablePen = &commonpb.Ref{Id: proto.String("pen")}
		},
	} {
		v := animalWire()
		mutate(v)
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("invalid animal census accepted", v)
		}
	}
	v := animalWire()
	v.Animals[0].RequiresPen = proto.Bool(false)
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal("pet rejected", err)
	}
}

func wildWire() *o.UpkeepFacts {
	v := upkeepWire()
	v.WildAnimals = []*o.AnimalFeed{{Pawn: &commonpb.Ref{Id: proto.String("wild")}, Diet: proto.String("OmnivoreAnimal"), RequiresPen: proto.Bool(false)}}
	return v
}

func TestWildAnimalUpkeepBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateDirectUpkeep(wildWire(), size, 3); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.UpkeepFacts){
		func(v *o.UpkeepFacts) { v.WildAnimals = append(v.WildAnimals, v.WildAnimals[0]) },
		func(v *o.UpkeepFacts) { v.WildAnimals[0].RequiresPen = proto.Bool(true) },
		func(v *o.UpkeepFacts) { v.WildAnimals[0].SuitablePen = &commonpb.Ref{Id: proto.String("pen")} },
	} {
		v := wildWire()
		mutate(v)
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("invalid wild census accepted", v)
		}
	}
}
