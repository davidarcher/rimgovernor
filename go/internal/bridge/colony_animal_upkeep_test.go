package bridge

import (
	"math"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func animalWire() *o.UpkeepFacts {
	v := upkeepWire()
	pawn := proto.Clone(v.Items[0].Item).(*o.EntityRef)
	pawn.Id = proto.String("animal")
	v.Animals = []*o.AnimalFeed{{Pawn: &o.PawnState{Pawn: pawn, AnimalState: &o.AnimalState{Contained: proto.Bool(false), Release: proto.Bool(false), Slaughter: proto.Bool(false)}}, RequiresPen: proto.Bool(true), ReachableStoredFeed: []*o.FoodStock{{Item: proto.Clone(v.Items[0].Item).(*o.EntityRef), Count: proto.Int64(1), HolderId: proto.String(""), Nutrition: proto.Float64(.5), EaterIds: []string{"animal"}, Perishable: proto.Bool(false)}}}}
	return v
}

func TestAnimalUpkeepBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateDirectUpkeep(animalWire(), size, 3); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.UpkeepFacts){
		func(v *o.UpkeepFacts) { v.Animals[0].Pawn.Pawn.MapId = proto.Int32(4) },
		func(v *o.UpkeepFacts) { v.Animals = append(v.Animals, v.Animals[0]) },
		func(v *o.UpkeepFacts) { v.Issues = []*o.ReadIssue{{Field: proto.String("animals")}} },
		func(v *o.UpkeepFacts) { v.Animals[0].RequiresPen = proto.Bool(false) },
		func(v *o.UpkeepFacts) { v.Animals[0].Pawn.AnimalState.PenId = proto.String("pen") },
		func(v *o.UpkeepFacts) { v.Animals[0].ReachableStoredFeed[0].Nutrition = proto.Float64(math.NaN()) },
		func(v *o.UpkeepFacts) { v.Animals[0].ReachableStoredFeed[0].Count = proto.Int64(-1) },
		func(v *o.UpkeepFacts) { v.Animals[0].ReachableStoredFeed[0].EaterIds = []string{"other"} },
		func(v *o.UpkeepFacts) { v.Animals[0].ReachableStoredFeed[0].HolderId = proto.String("animal") },
	} {
		v := animalWire()
		mutate(v)
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("invalid animal census accepted", v)
		}
	}
	v := animalWire()
	v.Animals[0].Pawn.AnimalState.Contained = nil
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal("unknown containment rejected", err)
	}
	v.Animals[0].RequiresPen = proto.Bool(false)
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal("pet rejected", err)
	}
	v.Animals[0].Pawn.AnimalState.SafeToRelease = proto.Bool(true)
	if err := validateDirectUpkeep(v, size, 3); err != nil {
		t.Fatal("release eligibility rejected", err)
	}
}

func wildWire() *o.UpkeepFacts {
	v := upkeepWire()
	pawn := proto.Clone(v.Items[0].Item).(*o.EntityRef)
	pawn.Id = proto.String("wild")
	v.WildAnimals = []*o.AnimalFeed{{Pawn: &o.PawnState{Pawn: pawn, Wild: proto.Bool(true), AnimalState: &o.AnimalState{Tameable: proto.Bool(true), Tame: proto.Bool(false), MinimumHandlingSkill: proto.Int32(3)}}, Diet: proto.String("OmnivoreAnimal"), RequiresPen: proto.Bool(false)}}
	return v
}

func TestWildAnimalUpkeepBoundary(t *testing.T) {
	size := &o.MapSize{Width: proto.Uint32(50), Height: proto.Uint32(50)}
	if err := validateDirectUpkeep(wildWire(), size, 3); err != nil {
		t.Fatal(err)
	}
	for _, mutate := range []func(*o.UpkeepFacts){
		func(v *o.UpkeepFacts) { v.WildAnimals = append(v.WildAnimals, v.WildAnimals[0]) },
		func(v *o.UpkeepFacts) { v.WildAnimals[0].Pawn.Wild = proto.Bool(false) },
		func(v *o.UpkeepFacts) { v.WildAnimals[0].Pawn.AnimalState = nil },
		func(v *o.UpkeepFacts) { v.WildAnimals[0].Pawn.AnimalState.Release = proto.Bool(false) },
		func(v *o.UpkeepFacts) { v.WildAnimals[0].Pawn.AnimalState.PenId = proto.String("pen") },
		func(v *o.UpkeepFacts) { v.WildAnimals[0].Pawn.AnimalState.MinimumHandlingSkill = proto.Int32(-1) },
		func(v *o.UpkeepFacts) { v.WildAnimals[0].RequiresPen = proto.Bool(true) },
		func(v *o.UpkeepFacts) { v.WildAnimals[0].SuitablePenId = proto.String("pen") },
		func(v *o.UpkeepFacts) {
			v.WildAnimals[0].ReachableStoredFeed = animalWire().Animals[0].ReachableStoredFeed
		},
	} {
		v := wildWire()
		mutate(v)
		if err := validateDirectUpkeep(v, size, 3); err == nil {
			t.Fatal("invalid wild census accepted", v)
		}
	}
}
