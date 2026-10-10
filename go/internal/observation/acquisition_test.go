package observation

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

// A census row carries its designation age and taken flag.
func TestColonyAcquisitionMapsDesignationAgeAndTaken(t *testing.T) {
	v := &o.ColonyFactsSnapshot{HuntCensus: huntCensusFacts(true).HuntCensus, Acquisition: []*o.AcquisitionFacts{
		{Source: bridge.NewRef("deer"), Resource: proto.String("Corpse_Deer"), Hunt: proto.Bool(true), MeatAmount: proto.Float64(100), Fogged: proto.Bool(false), InMentalState: proto.Bool(false), Designated: proto.Bool(true), DesignatedTick: proto.Int64(1200), Taken: proto.Bool(true)},
		{Source: bridge.NewRef("oak"), Designated: proto.Bool(false), Taken: proto.Bool(false)},
	}}
	rows, ok := ColonyAcquisition(v, huntTables(t, &o.EntityRef{Id: proto.String("deer"), DefName: proto.String("Deer")}, &o.EntityRef{Id: proto.String("oak")})).Value()
	if !ok || len(rows) != 2 {
		t.Fatal(rows, ok)
	}
	if rows[0].DesignatedTick != domain.Tick(1200) || !rows[0].Taken || !rows[0].Designated {
		t.Fatal(rows[0])
	}
	if rows[1].DesignatedTick != 0 || rows[1].Taken {
		t.Fatal(rows[1])
	}
}

// The hunt and plant rows' derived facts are the def rows' (the native census
// used to send each beside the live row): the race's predator flag and
// manhunter chance, the meat's nutrition times the live MeatAmount, whether a
// colonist can run the prey down by its life-stage body size, whether a plant
// is a tree and what its harvest feeds.
func TestAcquisitionRowsDeriveFromTheDefRows(t *testing.T) {
	rows := recordedRows(t, "Gun_BoltActionRifle", "Deer", "Hare", "Plant_TreeOak", "Plant_Berry", "RawBerries", "WoodLog")
	rows.AddSets("damage_defs")
	catalog := decodeRows(rows)
	races, err := catalog.AnimalRaces()
	if err != nil {
		t.Fatal(err)
	}
	deer, _ := races.Race("Deer")
	hare, _ := races.Race("Hare")
	adult := func(race policy.AnimalRace) *int32 {
		n := int32(len(race.LifeStages) - 1)
		return &n
	}
	tables := heads(&o.EntityRef{Id: proto.String("oak"), DefName: proto.String("Plant_TreeOak")}, &o.EntityRef{Id: proto.String("berry"), DefName: proto.String("Plant_Berry")})
	tables.Catalog = catalog
	tables.Pawns = bridge.NewPawns(
		&o.PawnState{Pawn: &o.EntityRef{Id: proto.String("deer"), DefName: proto.String("Deer")}, AnimalState: &o.AnimalState{LifeStageIndex: adult(deer)}},
		&o.PawnState{Pawn: &o.EntityRef{Id: proto.String("hare"), DefName: proto.String("Hare")}, AnimalState: &o.AnimalState{LifeStageIndex: adult(hare)}})
	v := huntCensusFacts(true)
	v.HuntCensus.Hunters[0].Routes = []*o.HuntRoute{{PreyId: proto.String("deer"), Safe: proto.Bool(true)}, {PreyId: proto.String("hare"), Safe: proto.Bool(true)}}
	v.HuntCensus.Benches[0].Bills[0].AllowedCorpses = []string{"Corpse_Deer", "Corpse_Hare"}
	hunt := func(id, corpse string, meat float64, downed bool) *o.AcquisitionFacts {
		return &o.AcquisitionFacts{Source: bridge.NewRef(id), Resource: proto.String(corpse), Hunt: proto.Bool(true), Yield: proto.Float64(1), MeatAmount: proto.Float64(meat), Downed: proto.Bool(downed), Fogged: proto.Bool(false), InMentalState: proto.Bool(false), Designated: proto.Bool(false), Taken: proto.Bool(false)}
	}
	v.Acquisition = []*o.AcquisitionFacts{
		hunt("deer", "Corpse_Deer", 100, false), hunt("hare", "Corpse_Hare", 20, false), hunt("deer", "Corpse_Deer", 100, true),
		{Source: bridge.NewRef("oak"), Resource: proto.String("WoodLog"), Hunt: proto.Bool(false), Food: proto.Bool(false), Yield: proto.Float64(10), Designated: proto.Bool(false), Taken: proto.Bool(false)},
		{Source: bridge.NewRef("berry"), Resource: proto.String("RawBerries"), Hunt: proto.Bool(false), Food: proto.Bool(true), Yield: proto.Float64(4), Designated: proto.Bool(false), Taken: proto.Bool(false)},
	}
	got, ok := ColonyAcquisition(v, tables).Value()
	if !ok || len(got) != 5 {
		t.Fatal(got, ok)
	}
	perUnit, _ := deer.MeatNutritionPerUnit.Value()
	chance, _ := deer.ManhunterOnDamage.Value()
	if got[0].NutritionYield != 100*perUnit || got[0].RevengeChance != chance || got[0].Predator != deer.Predator || !got[0].Food || got[0].Tree {
		t.Errorf("deer %+v", got[0])
	}
	if got[0].MeleeOnly || !got[1].MeleeOnly {
		t.Errorf("a docile adult deer (body size 1.2) is not run down by melee but a hare is: %+v %+v", got[0], got[1])
	}
	if !got[2].MeleeOnly {
		t.Errorf("a downed deer is run down by melee: %+v", got[2])
	}
	if !got[3].Tree || got[3].Food || got[3].NutritionYield != 0 {
		t.Errorf("oak %+v", got[3])
	}
	nutrition, _, _ := tables.Catalog.ShownStatValue("RawBerries", "", bridge.StatNutrition)
	if got[4].Tree || !got[4].Food || got[4].NutritionYield != 4*float64(nutrition) || nutrition <= 0 {
		t.Errorf("berry %+v", got[4])
	}
}
