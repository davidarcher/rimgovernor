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

func TestWallStuffTable(t *testing.T) {
	cases := []struct {
		name  string
		tier  BuildTier
		part  WallPart
		stock TierStyleStock
		want  Resource
		ok    bool
	}{
		{"camp wood", BuildTierCamp, WallRun, campStock, "WoodLog", true},
		{"camp without wood", BuildTierCamp, WallRun, tierStock(), "", false},
		{"camp never stone", BuildTierCamp, WallCorner, fullStock, "WoodLog", true},
		{"masonry quarried stone", BuildTierMasonry, WallRun, stoneStock, "BlocksGranite", true},
		{"masonry falls to wood", BuildTierMasonry, WallRun, campStock, "WoodLog", true},
		{"masonry nothing stocked", BuildTierMasonry, WallRun, tierStock(), "", false},
		{"powered stone", BuildTierPowered, WallDoorFrame, stoneStock, "BlocksGranite", true},
		{"powered without stone stays unproposed", BuildTierPowered, WallRun, campStock, "", false},
		{"industrial run is stone", BuildTierIndustrial, WallRun, steelStock, "BlocksGranite", true},
		{"industrial corner is steel", BuildTierIndustrial, WallCorner, steelStock, "Steel", true},
		{"industrial door frame is steel", BuildTierIndustrial, WallDoorFrame, steelStock, "Steel", true},
		{"industrial accent falls to stone", BuildTierIndustrial, WallCorner, stoneStock, "BlocksGranite", true},
		{"spacer plasteel", BuildTierSpacer, WallRun, fullStock, "Plasteel", true},
		{"spacer corner falls to steel", BuildTierSpacer, WallCorner, steelStock, "Steel", true},
		{"spacer run falls to stone", BuildTierSpacer, WallRun, steelStock, "BlocksGranite", true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, ok := WallStuff(tc.tier, tc.part, tc.stock)
			if ok != tc.ok || got != tc.want {
				t.Fatalf("WallStuff = %q %v, want %q %v", got, ok, tc.want, tc.ok)
			}
		})
	}
}

func TestFloorDefTable(t *testing.T) {
	carpet := FloorStyleFacts{CarpetMaking: true}
	sterile := FloorStyleFacts{SterileMaterials: true}
	cases := []struct {
		name  string
		tier  BuildTier
		role  RoomRole
		stock TierStyleStock
		facts FloorStyleFacts
		want  string
		ok    bool
	}{
		{"camp never floors", BuildTierCamp, RoomRoleBedroom, fullStock, FloorStyleFacts{true, true}, "", false},
		{"masonry aisle flagstone", BuildTierMasonry, RoomRoleNone, stoneStock, FloorStyleFacts{}, "FlagstoneGranite", true},
		{"masonry storage flagstone", BuildTierMasonry, RoomRoleStoreroom, stoneStock, FloorStyleFacts{}, "FlagstoneGranite", true},
		{"masonry bedroom tile", BuildTierMasonry, RoomRoleBedroom, stoneStock, FloorStyleFacts{}, "TileGranite", true},
		{"masonry dining tile", BuildTierMasonry, RoomRoleDiningRoom, stoneStock, FloorStyleFacts{}, "TileGranite", true},
		{"masonry workshop tile", BuildTierMasonry, RoomRoleWorkshop, stoneStock, FloorStyleFacts{}, "TileGranite", true},
		{"masonry hospital tile", BuildTierMasonry, RoomRoleHospital, stoneStock, sterile, "TileGranite", true},
		{"masonry without blocks falls to nothing", BuildTierMasonry, RoomRoleBedroom, campStock, FloorStyleFacts{}, "", false},
		{"masonry tomb unfloored", BuildTierMasonry, RoomRoleTomb, stoneStock, FloorStyleFacts{}, "", false},
		{"masonry carpet with cloth", BuildTierMasonry, RoomRoleBedroom, steelStock, carpet, Carpet, true},
		{"masonry recreation carpet", BuildTierMasonry, RoomRoleRecRoom, steelStock, carpet, Carpet, true},
		{"masonry carpet without cloth falls to tile", BuildTierMasonry, RoomRoleBedroom, stoneStock, carpet, "TileGranite", true},
		{"masonry carpet never in dining", BuildTierMasonry, RoomRoleDiningRoom, steelStock, carpet, "TileGranite", true},
		{"powered stone", BuildTierPowered, RoomRoleKitchen, stoneStock, FloorStyleFacts{}, "TileGranite", true},
		{"industrial sterile hospital", BuildTierIndustrial, RoomRoleHospital, steelStock, sterile, "SterileTile", true},
		{"industrial hospital without research falls to tile", BuildTierIndustrial, RoomRoleHospital, steelStock, FloorStyleFacts{}, "TileGranite", true},
		{"industrial hospital without silver falls to tile", BuildTierIndustrial, RoomRoleHospital, tierStock("BlocksGranite", 50, "Steel", 50), sterile, "TileGranite", true},
		{"industrial hospital without any stock", BuildTierIndustrial, RoomRoleHospital, campStock, sterile, "", false},
		{"spacer sterile", BuildTierSpacer, RoomRoleHospital, fullStock, sterile, "SterileTile", true},
		{"spacer hospital unmet falls to tile", BuildTierSpacer, RoomRoleHospital, stoneStock, sterile, "TileGranite", true},
		{"spacer aisle flagstone", BuildTierSpacer, RoomRoleNone, fullStock, FloorStyleFacts{}, "FlagstoneGranite", true},
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

func TestDoorDefTable(t *testing.T) {
	cases := []struct {
		name               string
		tier               BuildTier
		stock              TierStyleStock
		autodoors, powered bool
		want               DoorStyle
		ok                 bool
	}{
		{"camp wood door", BuildTierCamp, campStock, false, false, DoorStyle{"Door", "WoodLog"}, true},
		{"camp never autodoor", BuildTierCamp, fullStock, true, true, DoorStyle{"Door", "WoodLog"}, true},
		{"camp without wood", BuildTierCamp, tierStock(), false, false, DoorStyle{}, false},
		{"masonry stone door", BuildTierMasonry, stoneStock, false, false, DoorStyle{"Door", "BlocksGranite"}, true},
		{"masonry falls to wood", BuildTierMasonry, campStock, false, false, DoorStyle{"Door", "WoodLog"}, true},
		{"powered stone door", BuildTierPowered, stoneStock, true, true, DoorStyle{"Door", "BlocksGranite"}, true},
		{"industrial steel door", BuildTierIndustrial, steelStock, false, true, DoorStyle{"Door", "Steel"}, true},
		{"industrial autodoor", BuildTierIndustrial, steelStock, true, true, DoorStyle{"Autodoor", "Steel"}, true},
		{"industrial autodoor without power", BuildTierIndustrial, steelStock, true, false, DoorStyle{"Door", "Steel"}, true},
		{"industrial autodoor short of steel", BuildTierIndustrial, tierStock("Steel", 30), true, true, DoorStyle{"Door", "Steel"}, true},
		{"industrial without steel falls to stone", BuildTierIndustrial, stoneStock, true, true, DoorStyle{"Door", "BlocksGranite"}, true},
		{"spacer plasteel autodoor", BuildTierSpacer, fullStock, true, true, DoorStyle{"Autodoor", "Plasteel"}, true},
		{"spacer without plasteel is steel", BuildTierSpacer, steelStock, true, true, DoorStyle{"Autodoor", "Steel"}, true},
		{"spacer without metal falls to stone", BuildTierSpacer, stoneStock, true, true, DoorStyle{}, false},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, ok := DoorDef(tc.tier, tc.stock, tc.autodoors, tc.powered)
			if ok != tc.ok || got != tc.want {
				t.Fatalf("DoorDef = %+v %v, want %+v %v", got, ok, tc.want, tc.ok)
			}
		})
	}
}

func TestModuleLightingTable(t *testing.T) {
	cases := []struct {
		name    string
		tier    BuildTier
		farm    bool
		stock   TierStyleStock
		powered bool
		want    LightingStyle
		ok      bool
	}{
		{"camp unlit", BuildTierCamp, false, fullStock, true, LightingStyle{}, false},
		{"masonry torch per module", BuildTierMasonry, false, campStock, false, LightingStyle{"TorchLamp", LightingPerModule}, true},
		{"masonry torch without wood", BuildTierMasonry, false, tierStock("BlocksGranite", 50), false, LightingStyle{}, false},
		{"masonry farm unlit", BuildTierMasonry, true, campStock, false, LightingStyle{}, false},
		{"powered lamp per sub-cell", BuildTierPowered, false, steelStock, true, LightingStyle{"StandingLamp", LightingPerSubCell}, true},
		{"powered farm sun lamp", BuildTierPowered, true, steelStock, true, LightingStyle{"SunLamp", LightingPerModule}, true},
		{"powered without power falls to torch", BuildTierPowered, false, steelStock, false, LightingStyle{"TorchLamp", LightingPerModule}, true},
		{"powered without steel falls to torch", BuildTierPowered, false, stoneStock, true, LightingStyle{"TorchLamp", LightingPerModule}, true},
		{"powered farm without power unlit", BuildTierPowered, true, steelStock, false, LightingStyle{}, false},
		{"industrial lamp", BuildTierIndustrial, false, steelStock, true, LightingStyle{"StandingLamp", LightingPerSubCell}, true},
		{"spacer sun lamp", BuildTierSpacer, true, fullStock, true, LightingStyle{"SunLamp", LightingPerModule}, true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, ok := ModuleLighting(tc.tier, tc.farm, tc.stock, tc.powered)
			if ok != tc.ok || got != tc.want {
				t.Fatalf("ModuleLighting = %+v %v, want %+v %v", got, ok, tc.want, tc.ok)
			}
		})
	}
}
