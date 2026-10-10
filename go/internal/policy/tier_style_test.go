package policy

import "testing"

// tierStock builds the census the tier-style tables read.
func tierStock(pairs ...any) TierStyleStock {
	rows := make([]Amount, 0, len(pairs)/2)
	for i := 0; i+1 < len(pairs); i += 2 {
		rows = append(rows, Amount{Resource: Resource(pairs[i].(string)), Count: int64(pairs[i+1].(int))})
	}
	return TierStyleStockOf(rows)
}

var (
	campStock  = tierStock("WoodLog", 300)
	stoneStock = tierStock("WoodLog", 300, "BlocksGranite", 200, "BlocksSlate", 50)
	steelStock = tierStock("WoodLog", 300, "BlocksGranite", 200, "Steel", 500, "Silver", 100, "Cloth", 100)
	hayStock   = tierStock("WoodLog", 300, "BlocksGranite", 200, "Hay", 100)
	fullStock  = tierStock("WoodLog", 300, "BlocksGranite", 200, "Steel", 500, "Plasteel", 100, "Silver", 100, "Cloth", 100)
)

func TestTierStyleStock(t *testing.T) {
	stock := TierStyleStockOf([]Amount{{"BlocksSlate", 12}, {"BlocksGranite", 5}, {"BlocksGranite", 5}, {"Steel", -3}})
	if stone, ok := stock.QuarriedStone(); !ok || stone != "BlocksSlate" {
		t.Fatalf("quarried stone = %q %v, want BlocksSlate", stone, ok)
	}
	if stone, ok := tierStock("BlocksGranite", 5, "BlocksSlate", 5).QuarriedStone(); !ok || stone != "BlocksGranite" {
		t.Fatalf("tie broke to %q, want BlocksGranite", stone)
	}
	if _, ok := campStock.QuarriedStone(); ok {
		t.Fatal("wood-only stock has quarried stone")
	}
	if stock["Steel"] != 0 {
		t.Fatal("negative rows counted")
	}
}
func TestFloorDefTable(t *testing.T) {
	carpet := FloorStyleFacts{CarpetMaking: true, Costs: floorCosts}
	sterile := FloorStyleFacts{SterileMaterials: true, Costs: floorCosts}
	cases := []struct {
		name  string
		tier  TechTier
		role  RoomRole
		stock TierStyleStock
		facts FloorStyleFacts
		want  string
		ok    bool
	}{
		{"camp never floors", TechTierCamp, RoomRoleBedroom, fullStock, FloorStyleFacts{CarpetMaking: true, SterileMaterials: true, Costs: floorCosts}, "", false},
		{"masonry aisle flagstone", TechTierMasonry, RoomRoleNone, stoneStock, FloorStyleFacts{}, "FlagstoneGranite", true},
		{"masonry storage flagstone", TechTierMasonry, RoomRoleStoreroom, stoneStock, FloorStyleFacts{}, "FlagstoneGranite", true},
		{"masonry bedroom tile", TechTierMasonry, RoomRoleBedroom, stoneStock, FloorStyleFacts{}, "TileGranite", true},
		{"masonry dining tile", TechTierMasonry, RoomRoleDiningRoom, stoneStock, FloorStyleFacts{}, "TileGranite", true},
		{"masonry workshop tile", TechTierMasonry, RoomRoleWorkshop, stoneStock, FloorStyleFacts{}, "TileGranite", true},
		{"masonry hospital tile", TechTierMasonry, RoomRoleHospital, stoneStock, sterile, "TileGranite", true},
		{"masonry without blocks falls to nothing", TechTierMasonry, RoomRoleBedroom, campStock, FloorStyleFacts{}, "", false},
		{"masonry tomb unfloored", TechTierMasonry, RoomRoleTomb, stoneStock, FloorStyleFacts{}, "", false},
		{"masonry carpet with cloth", TechTierMasonry, RoomRoleBedroom, steelStock, carpet, Carpet, true},
		{"masonry recreation carpet", TechTierMasonry, RoomRoleRecRoom, steelStock, carpet, Carpet, true},
		{"masonry carpet without cloth falls to tile", TechTierMasonry, RoomRoleBedroom, stoneStock, carpet, "TileGranite", true},
		{"masonry carpet never in dining", TechTierMasonry, RoomRoleDiningRoom, steelStock, carpet, "TileGranite", true},
		{"powered stone", TechTierPowered, RoomRoleKitchen, stoneStock, FloorStyleFacts{}, "TileGranite", true},
		{"industrial sterile hospital", TechTierIndustrial, RoomRoleHospital, steelStock, sterile, "SterileTile", true},
		{"industrial hospital without research falls to tile", TechTierIndustrial, RoomRoleHospital, steelStock, FloorStyleFacts{}, "TileGranite", true},
		{"industrial hospital without silver falls to tile", TechTierIndustrial, RoomRoleHospital, tierStock("BlocksGranite", 50, "Steel", 50), sterile, "TileGranite", true},
		{"industrial hospital without any stock", TechTierIndustrial, RoomRoleHospital, campStock, sterile, "", false},
		{"spacer sterile", TechTierSpacer, RoomRoleHospital, fullStock, sterile, "SterileTile", true},
		{"spacer hospital unmet falls to tile", TechTierSpacer, RoomRoleHospital, stoneStock, sterile, "TileGranite", true},
		{"industrial sterile kitchen", TechTierIndustrial, RoomRoleKitchen, steelStock, sterile, "SterileTile", true},
		{"industrial kitchen without research falls to tile", TechTierIndustrial, RoomRoleKitchen, steelStock, FloorStyleFacts{}, "TileGranite", true},
		{"masonry barn straw matting", TechTierMasonry, RoomRoleBarn, hayStock, FloorStyleFacts{Costs: floorCosts}, StrawMatting, true},
		{"masonry barn without hay unfloored", TechTierMasonry, RoomRoleBarn, stoneStock, FloorStyleFacts{Costs: floorCosts}, "", false},
		{"camp barn unfloored", TechTierCamp, RoomRoleBarn, hayStock, FloorStyleFacts{Costs: floorCosts}, "", false},
		{"spacer aisle flagstone", TechTierSpacer, RoomRoleNone, fullStock, FloorStyleFacts{}, "FlagstoneGranite", true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, ok := FloorDef(tc.tier, tc.role, tc.stock, tc.facts)
			if ok != tc.ok || got != tc.want {
				t.Fatalf("FloorDef = %q %v, want %q %v", got, ok, tc.want, tc.ok)
			}
		})
	}
}

func TestModuleLightingTable(t *testing.T) {
	cases := []struct {
		name    string
		tier    TechTier
		farm    bool
		stock   TierStyleStock
		powered bool
		want    LightingStyle
		ok      bool
	}{
		{"camp unlit", TechTierCamp, false, fullStock, true, LightingStyle{}, false},
		{"masonry torch per module", TechTierMasonry, false, campStock, false, LightingStyle{"TorchLamp", LightingPerModule}, true},
		{"masonry torch without wood", TechTierMasonry, false, tierStock("BlocksGranite", 50), false, LightingStyle{}, false},
		{"masonry farm unlit", TechTierMasonry, true, campStock, false, LightingStyle{}, false},
		{"powered lamp per sub-cell", TechTierPowered, false, steelStock, true, LightingStyle{"StandingLamp", LightingPerSubCell}, true},
		{"powered farm sun lamp", TechTierPowered, true, steelStock, true, LightingStyle{"SunLamp", LightingPerModule}, true},
		{"powered without power falls to torch", TechTierPowered, false, steelStock, false, LightingStyle{"TorchLamp", LightingPerModule}, true},
		{"powered without steel falls to torch", TechTierPowered, false, stoneStock, true, LightingStyle{"TorchLamp", LightingPerModule}, true},
		{"powered farm without power unlit", TechTierPowered, true, steelStock, false, LightingStyle{}, false},
		{"industrial lamp", TechTierIndustrial, false, steelStock, true, LightingStyle{"StandingLamp", LightingPerSubCell}, true},
		{"spacer sun lamp", TechTierSpacer, true, fullStock, true, LightingStyle{"SunLamp", LightingPerModule}, true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, ok := PlannedLighting(tc.tier, tc.farm, tc.stock, tc.powered)
			if ok != tc.ok || got != tc.want {
				t.Fatalf("PlannedLighting = %+v %v, want %+v %v", got, ok, tc.want, tc.ok)
			}
		})
	}
}
