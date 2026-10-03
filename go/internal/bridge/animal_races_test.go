package bridge

import (
	"errors"
	"math"
	"testing"

	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func animalRaceRead() *o.AnimalRaceCatalog {
	return &o.AnimalRaceCatalog{
		Context: authorityTestContext(7),
		Races: []*o.AnimalRaceFacts{
			{DefName: proto.String("Muffalo"), CarryingCapacity: proto.Float64(100), Trainability: proto.String("Intermediate"), Trainables: []string{"Haul", "Obedience", "Release"},
				Wildness: proto.Float64(0.5), BodySize: proto.Float64(2.4), CombatPower: proto.Float64(100), MarketValue: proto.Float64(350), MinimumHandlingSkill: proto.Int32(5),
				Products: []*o.AnimalProduct{
					{Kind: proto.String("milk"), DefName: proto.String("Milk"), Amount: proto.Float64(8), IntervalDays: proto.Float64(2)},
					{Kind: proto.String("wool"), DefName: proto.String("WoolMuffalo"), Amount: proto.Float64(120), IntervalDays: proto.Float64(30)},
				}},
			{DefName: proto.String("Thrumbo")},
			{DefName: proto.String("Boomalope"), Products: []*o.AnimalProduct{{Kind: proto.String("spawner"), DefName: proto.String("Chemfuel"), Amount: proto.Float64(6), IntervalDays: proto.Float64(10)}}},
		},
	}
}

// TestDecodeAnimalRaceCatalog (#1625): a recorded catalog decodes into
// per-race facts, and an absent scalar stays unknown rather than zero.
func TestDecodeAnimalRaceCatalog(t *testing.T) {
	races, err := DecodeAnimalRaceCatalog(animalRaceRead(), pbIdentity())
	if err != nil {
		t.Fatal(err)
	}
	if races.LoadToken != "load" || len(races.Races) != 3 {
		t.Fatalf("catalog %+v", races)
	}
	muffalo, ok := races.Race("Muffalo")
	if !ok {
		t.Fatal("Muffalo missing")
	}
	if v, known := muffalo.CarryingCapacity.Value(); !known || v != 100 {
		t.Fatalf("carrying capacity %v %v", v, known)
	}
	if v, known := muffalo.MinimumHandlingSkill.Value(); !known || v != 5 {
		t.Fatalf("minimum handling skill %v %v", v, known)
	}
	if v, known := muffalo.Trainability.Value(); !known || v != "Intermediate" || len(muffalo.Trainables) != 3 {
		t.Fatalf("trainability %v %v %v", v, known, muffalo.Trainables)
	}
	if len(muffalo.Products) != 2 || muffalo.Products[1].Kind != "wool" || muffalo.Products[1].Def != "WoolMuffalo" {
		t.Fatalf("products %+v", muffalo.Products)
	}
	if v, known := muffalo.Products[0].IntervalDays.Value(); !known || v != 2 {
		t.Fatalf("milk interval %v %v", v, known)
	}
	thrumbo, _ := races.Race("Thrumbo")
	if _, known := thrumbo.CarryingCapacity.Value(); known {
		t.Fatal("absent carrying capacity read as known")
	}
	if _, known := thrumbo.MinimumHandlingSkill.Value(); known {
		t.Fatal("absent handling skill read as known")
	}
	if _, known := thrumbo.Trainability.Value(); known {
		t.Fatal("absent trainability read as known")
	}
	if _, known := thrumbo.MarketValue.Value(); known {
		t.Fatal("absent market value read as known")
	}
	if boom, _ := races.Race("Boomalope"); len(boom.Products) != 1 || boom.Products[0].Kind != "spawner" {
		t.Fatalf("chemfuel product %+v", boom.Products)
	}
	if _, ok := races.Race("Dodo"); ok {
		t.Fatal("unknown race found")
	}
}

func TestDecodeAnimalRaceCatalogRefusesMalformedRows(t *testing.T) {
	for name, edit := range map[string]func(*o.AnimalRaceCatalog){
		"no context":       func(v *o.AnimalRaceCatalog) { v.Context = nil },
		"unnamed race":     func(v *o.AnimalRaceCatalog) { v.Races[1].DefName = nil },
		"duplicate race":   func(v *o.AnimalRaceCatalog) { v.Races[1].DefName = proto.String("Muffalo") },
		"negative scalar":  func(v *o.AnimalRaceCatalog) { v.Races[0].BodySize = proto.Float64(-1) },
		"infinite scalar":  func(v *o.AnimalRaceCatalog) { v.Races[0].MarketValue = proto.Float64(math.Inf(1)) },
		"negative skill":   func(v *o.AnimalRaceCatalog) { v.Races[0].MinimumHandlingSkill = proto.Int32(-1) },
		"blank trainable":  func(v *o.AnimalRaceCatalog) { v.Races[0].Trainables = []string{" "} },
		"unnamed product":  func(v *o.AnimalRaceCatalog) { v.Races[0].Products[0].DefName = nil },
		"negative product": func(v *o.AnimalRaceCatalog) { v.Races[0].Products[0].Amount = proto.Float64(-2) },
	} {
		v := animalRaceRead()
		edit(v)
		if _, err := DecodeAnimalRaceCatalog(v, pbIdentity()); !errors.Is(err, ErrContract) {
			t.Errorf("%s: err %v", name, err)
		}
	}
}
