package bridge

import (
	"math"
	"testing"

	c "github.com/davidarcher/RimGovernor/go/internal/wire/commonpb"
	o "github.com/davidarcher/RimGovernor/go/internal/wire/observationspb"
	"google.golang.org/protobuf/proto"
)

func odysseyCatalogFixture() *o.OdysseyCatalog {
	return &o.OdysseyCatalog{
		BiomeAnimals: []*o.BiomeAnimals{
			{Biome: proto.String("Glowforest"), WildAnimals: []*o.BiomeAnimal{{Kind: proto.String("Muffalo"), Commonality: proto.Float64(1)}}},
			{Biome: proto.String("LavaField")},
		},
		StockpileTypes: []*o.StockpileTypeRow{{Name: proto.String("Gravcore"), Generatable: proto.Bool(false)}, {Name: proto.String("Medicine"), Generatable: proto.Bool(true)}},
	}
}

// TestOdysseyCatalogDecode (#1708): the section decodes by name and refuses
// duplicates and bad animal rows; absent stays nil.
func TestOdysseyCatalogDecode(t *testing.T) {
	got, err := DecodeOdysseyCatalog(odysseyCatalogFixture())
	if err != nil || got.BiomeAnimals["Glowforest"] == nil || got.StockpileTypes["Gravcore"] == nil {
		t.Fatalf("%+v %v", got, err)
	}
	if none, err := DecodeOdysseyCatalog(nil); none != nil || err != nil {
		t.Fatal("Core-only catalog must decode to nil", none, err)
	}
	for name, mutate := range map[string]func(*o.OdysseyCatalog){
		"duplicate biome":  func(v *o.OdysseyCatalog) { v.BiomeAnimals = append(v.BiomeAnimals, v.BiomeAnimals[0]) },
		"zero commonality": func(v *o.OdysseyCatalog) { v.BiomeAnimals[0].WildAnimals[0].Commonality = proto.Float64(0) },
		"duplicate animal": func(v *o.OdysseyCatalog) {
			v.BiomeAnimals[0].WildAnimals = append(v.BiomeAnimals[0].WildAnimals, v.BiomeAnimals[0].WildAnimals[0])
		},
		"nan commonality": func(v *o.OdysseyCatalog) { v.BiomeAnimals[0].WildAnimals[0].Commonality = proto.Float64(math.NaN()) },
	} {
		v := odysseyCatalogFixture()
		mutate(v)
		if _, err := DecodeOdysseyCatalog(v); err == nil {
			t.Errorf("%s accepted", name)
		}
	}
}

func odysseyBuildingFixture() *o.BuildingState {
	return &o.BuildingState{Building: &o.EntityRef{Id: proto.String("Building_Hatch1")}, Odyssey: &o.OdysseyBuilding{
		Hackable: &o.HackableState{ProgressPercent: proto.Float64(.25), Defence: proto.Float64(600), Hacked: proto.Bool(false), LockedOut: proto.Bool(false)},
		Portal:   &o.PortalState{PocketMapExists: proto.Bool(true), PocketMapId: proto.Int32(3), StockpileType: proto.String("Gravcore"), Layout: proto.String("AncientStockpile")},
	}}
}

// TestBuildingOdysseyRow (#1708): the row block lifts into typed facts, a
// field absent stays unknown, a failed sub-read is named by an issue, and
// malformed blocks are refused.
func TestBuildingOdysseyRow(t *testing.T) {
	row := odysseyBuildingFixture()
	if err := validateBuildingOdyssey(row.Odyssey); err != nil {
		t.Fatal(err)
	}
	hack, known := BuildingHack(row).Value()
	if !known {
		t.Fatal("hack unknown")
	}
	if p, ok := hack.ProgressPercent.Value(); !ok || p != .25 {
		t.Fatal("progress", p, ok)
	}
	if _, ok := hack.Autohack.Value(); ok {
		t.Fatal("an unread autohack flag must stay unknown")
	}
	portal, known := BuildingPortal(row).Value()
	if id, ok := portal.PocketMapID.Value(); !known || !ok || id != 3 {
		t.Fatal("portal", portal, known)
	}
	if kind, _ := portal.StockpileType.Value(); kind != "Gravcore" {
		t.Fatal("stockpile type", kind)
	}
	if _, ok := BuildingHack(&o.BuildingState{}).Value(); ok {
		t.Fatal("a Core-only row must stay unknown")
	}
	failed := &o.BuildingState{Odyssey: &o.OdysseyBuilding{Portal: row.Odyssey.Portal,
		Issues: []*o.ReadIssue{{Field: proto.String("hackable"), Unavailable: &c.Unavailable{Reason: c.UnavailableReason_UNAVAILABLE_REASON_READ_FAILED.Enum()}}}}}
	if err := validateBuildingOdyssey(failed.Odyssey); err != nil {
		t.Fatal(err)
	}
	if _, ok := BuildingHack(failed).Value(); ok {
		t.Fatal("a failed hack read must stay unknown")
	}
	for name, mutate := range map[string]func(*o.OdysseyBuilding){
		"progress range":     func(v *o.OdysseyBuilding) { v.Hackable.ProgressPercent = proto.Float64(1.5) },
		"nan progress":       func(v *o.OdysseyBuilding) { v.Hackable.ProgressPercent = proto.Float64(math.NaN()) },
		"map without pocket": func(v *o.OdysseyBuilding) { v.Portal.PocketMapExists = proto.Bool(false) },
		"known and issued":   func(v *o.OdysseyBuilding) { v.Issues = []*o.ReadIssue{{Field: proto.String("hackable")}} },
	} {
		r := odysseyBuildingFixture()
		mutate(r.Odyssey)
		if validateBuildingOdyssey(r.Odyssey) == nil {
			t.Errorf("%s accepted", name)
		}
	}
	if checkBuildingListRow(row) != nil {
		t.Fatal("list row refused")
	}
}
