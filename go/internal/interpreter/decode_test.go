package interpreter

import (
	"strings"
	"testing"
)

func TestModelResponseShape(t *testing.T) {
	for _, text := range []string{
		`null`, `[]`, `{}`, `{"command":null}`, `{"command":true}`,
		`{"command":"build"}`, `{"command":"build","buildings":null}`,
		`{"command":"build","buildings":[]}`,
		`{"command":"build","buildings":[null]}`,
		`{"command":"build","buildings":[true]}`,
		valid + `{}`, valid + ` null`, valid + ` garbage`,
		strings.Replace(valid, `"x":2`, `"x":2,"\u0078":3`, 1),
		strings.Replace(valid, `"x":2,`, ``, 1),
		strings.Replace(valid, `"x":2`, `"x":null`, 1),
		strings.Replace(valid, `"x":2`, `"x":"2"`, 1),
		strings.Replace(valid, `"x":2`, `"x":true`, 1),
		strings.Replace(valid, `"x":2`, `"x":2147483648`, 1),
		strings.Replace(valid, `"z":3`, `"z":-2147483649`, 1),
		strings.Replace(valid, `"stuff":"Granite"`, `"stuff":{}`, 1),
		strings.Replace(valid, `"stuff":"Granite"`, `"Stuff":"Granite"`, 1),
		strings.Replace(valid, `"stuff":"Granite"`, `"stuff":"Granite","actionId":"invented"`, 1),
		strings.Replace(valid, `"buildings":`, `"buildings":`+strings.Repeat(`[`, 20), 1),
		strings.Replace(valid, `Wall`, string([]byte{0xff}), 1),
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 4)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelIntegerBoundsAndActionLimit(t *testing.T) {
	text := strings.Replace(valid, `"x":2,"z":3`, `"x":-2147483648,"z":2147483647`, 1)
	result, err := decode(text, 1)
	if err != nil || len(result.Buildings) != 1 || *result.Buildings[0].X != -2147483648 || *result.Buildings[0].Z != 2147483647 {
		t.Fatalf("integer bounds: %v %v", result, err)
	}
	// Shape decoding accepts int32 coordinates; Interpret separately requires
	// nonnegative, observed anchors inside the current map.
	building := `{"defName":"Wall","x":2,"z":3,"rotation":"north","stuff":"Granite"}`
	_, err = decode(`{"command":"build","buildings":[`+building+`,`+building+`]}`, 1)
	assertKind(t, err, InvalidCommand)
}

func TestModelTextUsesStandardJSONDecoding(t *testing.T) {
	// Native transport grammar and text limits do not own model responses.
	// Catalog matching and domain constructors validate the decoded proposal.
	text := strings.Replace(valid, `Wall`, `Modded\uD83C\uDFE0`, 1)
	result, err := decode(text, 1)
	if err != nil || *result.Buildings[0].DefName != "Modded🏠" {
		t.Fatalf("Unicode text: %v %v", result, err)
	}
	text = strings.Replace(valid, `Wall`, strings.Repeat("a", 240), 1)
	if _, err := decode(text, 1); err != nil {
		t.Fatalf("model decoder retained native UTF16 limit: %v", err)
	}
}

func TestModelResearchShape(t *testing.T) {
	result, err := decode(`{"command":"research","project":"Electricity"}`, 1)
	if err != nil || result.Command != "research" || result.Project == nil || *result.Project != "Electricity" {
		t.Fatalf("research shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"research"}`,
		`{"command":"research","project":null}`,
		`{"command":"research","project":""}`,
		`{"command":"research","project":2}`,
		`{"command":"research","project":"Electricity","dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelTendAndRescueShape(t *testing.T) {
	result, err := decode(`{"command":"tend","doctor":"Thing_Doc","patient":"Thing_Pat"}`, 1)
	if err != nil || result.Command != "tend" || result.First == nil || *result.First != "Thing_Doc" || result.Second == nil || *result.Second != "Thing_Pat" {
		t.Fatalf("tend shape: %v %v", result, err)
	}
	result, err = decode(`{"command":"rescue","rescuer":"Thing_Res","patient":"Thing_Pat"}`, 1)
	if err != nil || result.Command != "rescue" || result.First == nil || *result.First != "Thing_Res" || result.Second == nil || *result.Second != "Thing_Pat" {
		t.Fatalf("rescue shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"tend"}`,
		`{"command":"tend","doctor":"Thing_Doc"}`,
		`{"command":"tend","doctor":null,"patient":"Thing_Pat"}`,
		`{"command":"tend","doctor":"","patient":"Thing_Pat"}`,
		`{"command":"tend","doctor":2,"patient":"Thing_Pat"}`,
		`{"command":"tend","doctor":"Thing_Doc","patient":"Thing_Pat","dryRun":false}`,
		`{"command":"rescue","doctor":"Thing_Doc","patient":"Thing_Pat"}`,
		`{"command":"rescue","rescuer":"Thing_Res"}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelDraftShape(t *testing.T) {
	result, err := decode(`{"command":"draft","pawn":"Thing_A"}`, 1)
	if err != nil || result.Command != "draft" || result.First == nil || *result.First != "Thing_A" {
		t.Fatalf("draft shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"draft"}`,
		`{"command":"draft","pawn":null}`,
		`{"command":"draft","pawn":""}`,
		`{"command":"draft","pawn":2}`,
		`{"command":"draft","pawn":"Thing_A","dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelHusbandryShape(t *testing.T) {
	result, err := decode(`{"command":"husbandry","animal":"Thing_A","method":"train","trainableDef":"Sit"}`, 1)
	if err != nil || result.Command != "husbandry" || result.Animal == nil || *result.Animal != "Thing_A" ||
		result.Method == nil || *result.Method != "train" || result.TrainableDef == nil || *result.TrainableDef != "Sit" {
		t.Fatalf("husbandry train shape: %v %v", result, err)
	}
	result, err = decode(`{"command":"husbandry","animal":"Thing_A","method":"slaughter"}`, 1)
	if err != nil || result.Command != "husbandry" || result.Animal == nil || *result.Animal != "Thing_A" ||
		result.Method == nil || *result.Method != "slaughter" || result.TrainableDef != nil {
		t.Fatalf("husbandry slaughter shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"husbandry"}`,
		`{"command":"husbandry","animal":"Thing_A"}`,
		`{"command":"husbandry","animal":"","method":"train","trainableDef":"Sit"}`,
		`{"command":"husbandry","animal":null,"method":"train","trainableDef":"Sit"}`,
		`{"command":"husbandry","animal":"Thing_A","method":"train"}`,
		`{"command":"husbandry","animal":"Thing_A","method":"train","trainableDef":null}`,
		`{"command":"husbandry","animal":"Thing_A","method":"train","trainableDef":""}`,
		`{"command":"husbandry","animal":"Thing_A","method":"slaughter","trainableDef":"Sit"}`,
		`{"command":"husbandry","animal":"Thing_A","method":"tame"}`,
		`{"command":"husbandry","animal":"Thing_A","method":"slaughter","dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelRecoveryServiceShape(t *testing.T) {
	result, err := decode(`{"command":"recover","pawn":"Thing_A","thing":"Thing_B","method":"repair"}`, 1)
	if err != nil || result.Command != "recover" || result.Pawn == nil || *result.Pawn != "Thing_A" ||
		result.Thing == nil || *result.Thing != "Thing_B" || result.Service == nil || *result.Service != "repair" {
		t.Fatalf("recover shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"recover"}`,
		`{"command":"recover","pawn":"Thing_A"}`,
		`{"command":"recover","pawn":"Thing_A","thing":"Thing_B"}`,
		`{"command":"recover","pawn":null,"thing":"Thing_B","method":"repair"}`,
		`{"command":"recover","pawn":"","thing":"Thing_B","method":"repair"}`,
		`{"command":"recover","pawn":"Thing_A","thing":"","method":"repair"}`,
		`{"command":"recover","pawn":"Thing_A","thing":"Thing_B","method":""}`,
		`{"command":"recover","pawn":"Thing_A","thing":"Thing_B","method":"reboot"}`,
		`{"command":"recover","pawn":"Thing_A","thing":"Thing_B","method":"repair","dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelBedAssignShape(t *testing.T) {
	result, err := decode(`{"command":"bed_assign","pawn":"Thing_A","bed":"Thing_Bed1"}`, 1)
	if err != nil || result.Command != "bed_assign" || result.Pawn == nil || *result.Pawn != "Thing_A" ||
		result.Bed == nil || *result.Bed != "Thing_Bed1" {
		t.Fatalf("bed_assign shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"bed_assign"}`,
		`{"command":"bed_assign","pawn":"Thing_A"}`,
		`{"command":"bed_assign","pawn":null,"bed":"Thing_Bed1"}`,
		`{"command":"bed_assign","pawn":"","bed":"Thing_Bed1"}`,
		`{"command":"bed_assign","pawn":"Thing_A","bed":""}`,
		`{"command":"bed_assign","pawn":"Thing_A","bed":"Thing_Bed1","dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelMoveShape(t *testing.T) {
	result, err := decode(`{"command":"move_pawn","pawn":"Thing_A","x":3,"z":4}`, 1)
	if err != nil || result.Command != "move_pawn" || result.Pawn == nil || *result.Pawn != "Thing_A" ||
		result.X == nil || *result.X != 3 || result.Z == nil || *result.Z != 4 {
		t.Fatalf("move_pawn shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"move_pawn"}`,
		`{"command":"move_pawn","pawn":"Thing_A"}`,
		`{"command":"move_pawn","pawn":"Thing_A","x":3}`,
		`{"command":"move_pawn","pawn":null,"x":3,"z":4}`,
		`{"command":"move_pawn","pawn":"","x":3,"z":4}`,
		`{"command":"move_pawn","pawn":"Thing_A","x":-1,"z":4}`,
		`{"command":"move_pawn","pawn":"Thing_A","x":3,"z":-1}`,
		`{"command":"move_pawn","pawn":"Thing_A","x":3,"z":4,"dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelSetBuildingTemperatureShape(t *testing.T) {
	result, err := decode(`{"command":"set_building_temperature","thing":"Thing_Heater1","celsius":21}`, 1)
	if err != nil || result.Command != "set_building_temperature" || result.Thing == nil || *result.Thing != "Thing_Heater1" ||
		result.Celsius == nil || *result.Celsius != 21 {
		t.Fatalf("set_building_temperature shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"set_building_temperature"}`,
		`{"command":"set_building_temperature","thing":"Thing_Heater1"}`,
		`{"command":"set_building_temperature","thing":null,"celsius":21}`,
		`{"command":"set_building_temperature","thing":"","celsius":21}`,
		`{"command":"set_building_temperature","thing":"Thing_Heater1","celsius":null}`,
		`{"command":"set_building_temperature","thing":"Thing_Heater1","celsius":"21"}`,
		`{"command":"set_building_temperature","thing":"Thing_Heater1","celsius":21,"dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelSurgeryShape(t *testing.T) {
	result, err := decode(`{"command":"request_surgery","patient":"Thing_A","recipe":"RemoveBodyPart","part":3}`, 1)
	if err != nil || result.Command != "request_surgery" || result.Pawn == nil || *result.Pawn != "Thing_A" ||
		result.Recipe == nil || *result.Recipe != "RemoveBodyPart" || result.Part == nil || *result.Part != 3 {
		t.Fatalf("request_surgery shape: %v %v", result, err)
	}
	wholeBody, err := decode(`{"command":"request_surgery","patient":"Thing_A","recipe":"InstallPegLeg","part":-1}`, 1)
	if err != nil || wholeBody.Part == nil || *wholeBody.Part != -1 {
		t.Fatalf("request_surgery whole-body shape: %v %v", wholeBody, err)
	}
	for _, text := range []string{
		`{"command":"request_surgery"}`,
		`{"command":"request_surgery","patient":"Thing_A"}`,
		`{"command":"request_surgery","patient":"Thing_A","recipe":"RemoveBodyPart"}`,
		`{"command":"request_surgery","patient":null,"recipe":"RemoveBodyPart","part":3}`,
		`{"command":"request_surgery","patient":"","recipe":"RemoveBodyPart","part":3}`,
		`{"command":"request_surgery","patient":"Thing_A","recipe":"","part":3}`,
		`{"command":"request_surgery","patient":"Thing_A","recipe":null,"part":3}`,
		`{"command":"request_surgery","patient":"Thing_A","recipe":"RemoveBodyPart","part":null}`,
		`{"command":"request_surgery","patient":"Thing_A","recipe":"RemoveBodyPart","part":-2}`,
		`{"command":"request_surgery","patient":"Thing_A","recipe":"RemoveBodyPart","part":3,"dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelCaravanShape(t *testing.T) {
	valid := `{"command":"caravan","crew":["Thing_A"],"cargo":[{"defName":"Silver","count":50}],"destinationTile":3}`
	result, err := decode(valid, 1)
	if err != nil || result.Command != "caravan" || len(result.Crew) != 1 || result.Crew[0] != "Thing_A" ||
		len(result.Cargo) != 1 || *result.Cargo[0].Definition != "Silver" || *result.Cargo[0].Count != 50 ||
		result.DestinationTile == nil || *result.DestinationTile != 3 {
		t.Fatalf("caravan shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"caravan","crew":[],"cargo":[{"defName":"Silver","count":50}],"destinationTile":3}`,
		`{"command":"caravan","crew":["Thing_A"],"cargo":[],"destinationTile":3}`,
		`{"command":"caravan","crew":["Thing_A"],"cargo":[{"defName":"Silver","count":50}]}`,
		`{"command":"caravan","crew":["Thing_A"],"cargo":[{"defName":"Silver","count":50}],"destinationTile":-1}`,
		`{"command":"caravan","crew":["Thing_A"],"cargo":[{"defName":"Silver","count":0}],"destinationTile":3}`,
		`{"command":"caravan","crew":["Thing_A"],"cargo":[{"defName":"","count":50}],"destinationTile":3}`,
		`{"command":"caravan","crew":[""],"cargo":[{"defName":"Silver","count":50}],"destinationTile":3}`,
		`{"command":"caravan","crew":["Thing_A"],"cargo":[{"defName":"Silver","count":-1}],"destinationTile":3}`,
		`{"command":"caravan","crew":["Thing_A"],"cargo":[{"defName":"Silver","count":50}],"destinationTile":3,"dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelHoldCaravanShape(t *testing.T) {
	result, err := decode(`{"command":"hold_caravan","caravan":"Caravan_A"}`, 1)
	if err != nil || result.Command != "hold_caravan" || result.Caravan == nil || *result.Caravan != "Caravan_A" {
		t.Fatalf("hold_caravan shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"hold_caravan"}`,
		`{"command":"hold_caravan","caravan":null}`,
		`{"command":"hold_caravan","caravan":""}`,
		`{"command":"hold_caravan","caravan":"Caravan_A","destinationTile":3}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelRouteCaravanShape(t *testing.T) {
	route := `{"command":"route_caravan","caravan":"Caravan_A","destinationTile":3,"returnHome":false,"visitSettlement":false}`
	result, err := decode(route, 1)
	if err != nil || result.Command != "route_caravan" || result.Caravan == nil || *result.Caravan != "Caravan_A" ||
		result.DestinationTile == nil || *result.DestinationTile != 3 ||
		result.ReturnHome == nil || *result.ReturnHome || result.VisitSettlement == nil || *result.VisitSettlement {
		t.Fatalf("route_caravan route shape: %v %v", result, err)
	}
	visit := `{"command":"route_caravan","caravan":"Caravan_A","destinationTile":3,"returnHome":false,"visitSettlement":true}`
	result, err = decode(visit, 1)
	if err != nil || result.VisitSettlement == nil || !*result.VisitSettlement {
		t.Fatalf("route_caravan visit shape: %v %v", result, err)
	}
	home := `{"command":"route_caravan","caravan":"Caravan_A","destinationTile":null,"returnHome":true,"visitSettlement":false}`
	result, err = decode(home, 1)
	if err != nil || result.DestinationTile != nil || result.ReturnHome == nil || !*result.ReturnHome {
		t.Fatalf("route_caravan return-home shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"route_caravan"}`,
		`{"command":"route_caravan","caravan":"Caravan_A"}`,
		`{"command":"route_caravan","caravan":"","destinationTile":3,"returnHome":false,"visitSettlement":false}`,
		`{"command":"route_caravan","caravan":"Caravan_A","destinationTile":-1,"returnHome":false,"visitSettlement":false}`,
		// exactly one of destinationTile/returnHome
		`{"command":"route_caravan","caravan":"Caravan_A","destinationTile":3,"returnHome":true,"visitSettlement":false}`,
		`{"command":"route_caravan","caravan":"Caravan_A","destinationTile":null,"returnHome":false,"visitSettlement":false}`,
		// visitSettlement requires a route, not return-home
		`{"command":"route_caravan","caravan":"Caravan_A","destinationTile":null,"returnHome":true,"visitSettlement":true}`,
		`{"command":"route_caravan","caravan":"Caravan_A","destinationTile":3,"returnHome":false,"visitSettlement":false,"dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelAcceptQuestShape(t *testing.T) {
	result, err := decode(`{"command":"accept_quest","quest":"Quest_A","accepterPawn":"Thing_A","rewardChoice":0}`, 1)
	if err != nil || result.Command != "accept_quest" || result.Quest == nil || *result.Quest != "Quest_A" ||
		result.AccepterPawn == nil || *result.AccepterPawn != "Thing_A" || result.RewardChoice == nil || *result.RewardChoice != 0 {
		t.Fatalf("accept_quest shape: %v %v", result, err)
	}
	noAccepter, err := decode(`{"command":"accept_quest","quest":"Quest_A","accepterPawn":"","rewardChoice":-1}`, 1)
	if err != nil || noAccepter.AccepterPawn == nil || *noAccepter.AccepterPawn != "" || noAccepter.RewardChoice == nil || *noAccepter.RewardChoice != -1 {
		t.Fatalf("accept_quest no-accepter shape: %v %v", noAccepter, err)
	}
	for _, text := range []string{
		`{"command":"accept_quest"}`,
		`{"command":"accept_quest","quest":"Quest_A"}`,
		`{"command":"accept_quest","quest":"Quest_A","accepterPawn":"Thing_A"}`,
		`{"command":"accept_quest","quest":null,"accepterPawn":"Thing_A","rewardChoice":0}`,
		`{"command":"accept_quest","quest":"","accepterPawn":"Thing_A","rewardChoice":0}`,
		`{"command":"accept_quest","quest":"Quest_A","accepterPawn":null,"rewardChoice":0}`,
		`{"command":"accept_quest","quest":"Quest_A","accepterPawn":"Thing_A","rewardChoice":null}`,
		`{"command":"accept_quest","quest":"Quest_A","accepterPawn":"Thing_A","rewardChoice":-2}`,
		`{"command":"accept_quest","quest":"Quest_A","accepterPawn":"Thing_A","rewardChoice":0,"dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelFulfillQuestShape(t *testing.T) {
	result, err := decode(`{"command":"fulfill_quest","quest":"Quest_A","caravan":"Caravan_A","crew":["Thing_A"]}`, 1)
	if err != nil || result.Command != "fulfill_quest" || result.Quest == nil || *result.Quest != "Quest_A" ||
		result.Caravan == nil || *result.Caravan != "Caravan_A" || len(result.Crew) != 1 || result.Crew[0] != "Thing_A" {
		t.Fatalf("fulfill_quest shape: %v %v", result, err)
	}
	for _, text := range []string{
		`{"command":"fulfill_quest"}`,
		`{"command":"fulfill_quest","quest":"Quest_A","caravan":"Caravan_A"}`,
		`{"command":"fulfill_quest","quest":null,"caravan":"Caravan_A","crew":["Thing_A"]}`,
		`{"command":"fulfill_quest","quest":"","caravan":"Caravan_A","crew":["Thing_A"]}`,
		`{"command":"fulfill_quest","quest":"Quest_A","caravan":"","crew":["Thing_A"]}`,
		`{"command":"fulfill_quest","quest":"Quest_A","caravan":"Caravan_A","crew":[]}`,
		`{"command":"fulfill_quest","quest":"Quest_A","caravan":"Caravan_A","crew":[""]}`,
		`{"command":"fulfill_quest","quest":"Quest_A","caravan":"Caravan_A","crew":["Thing_A"],"dryRun":false}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelGiftSettlementShape(t *testing.T) {
	text := `{"command":"gift_settlement","caravan":"Caravan_A","settlement":"Settlement_A","faction":"Faction_A","crew":["Thing_A"],"silver":100}`
	result, err := decode(text, 1)
	if err != nil || result.Command != "gift_settlement" || result.Caravan == nil || *result.Caravan != "Caravan_A" ||
		result.Settlement == nil || *result.Settlement != "Settlement_A" || result.Faction == nil || *result.Faction != "Faction_A" ||
		len(result.Crew) != 1 || result.Crew[0] != "Thing_A" || result.Silver == nil || *result.Silver != 100 {
		t.Fatalf("gift_settlement shape: %v %v", result, err)
	}
	for _, invalid := range []string{
		`{"command":"gift_settlement"}`,
		`{"command":"gift_settlement","caravan":"Caravan_A","settlement":"Settlement_A","faction":"Faction_A","crew":["Thing_A"]}`,
		`{"command":"gift_settlement","caravan":null,"settlement":"Settlement_A","faction":"Faction_A","crew":["Thing_A"],"silver":100}`,
		`{"command":"gift_settlement","caravan":"Caravan_A","settlement":"","faction":"Faction_A","crew":["Thing_A"],"silver":100}`,
		`{"command":"gift_settlement","caravan":"Caravan_A","settlement":"Settlement_A","faction":null,"crew":["Thing_A"],"silver":100}`,
		`{"command":"gift_settlement","caravan":"Caravan_A","settlement":"Settlement_A","faction":"Faction_A","crew":[],"silver":100}`,
		`{"command":"gift_settlement","caravan":"Caravan_A","settlement":"Settlement_A","faction":"Faction_A","crew":["Thing_A"],"silver":0}`,
		`{"command":"gift_settlement","caravan":"Caravan_A","settlement":"Settlement_A","faction":"Faction_A","crew":["Thing_A"],"silver":null}`,
		`{"command":"gift_settlement","caravan":"Caravan_A","settlement":"Settlement_A","faction":"Faction_A","crew":["Thing_A"],"silver":100,"dryRun":false}`,
	} {
		t.Run(invalid, func(t *testing.T) {
			_, err := decode(invalid, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelCreateZoneShape(t *testing.T) {
	growing, err := decode(`{"command":"create_zone","zoneKind":"growing","crop":"Rice","cells":[{"x":0,"z":0},{"x":1,"z":0}]}`, 1)
	if err != nil || growing.Command != "create_zone" || growing.ZoneKind == nil || *growing.ZoneKind != "growing" ||
		growing.Crop == nil || *growing.Crop != "Rice" || len(growing.ZoneCells) != 2 ||
		*growing.ZoneCells[0].X != 0 || *growing.ZoneCells[0].Z != 0 || *growing.ZoneCells[1].X != 1 || *growing.ZoneCells[1].Z != 0 {
		t.Fatalf("create_zone growing shape: %v %v", growing, err)
	}
	food, err := decode(`{"command":"create_zone","zoneKind":"stockpile","preset":"food","priority":"important","cells":[{"x":0,"z":0}]}`, 1)
	if err != nil || food.Command != "create_zone" || food.Preset == nil || *food.Preset != "food" || food.Priority == nil || *food.Priority != "important" || len(food.ZoneCells) != 1 {
		t.Fatalf("create_zone food stockpile shape: %v %v", food, err)
	}
	nothing, err := decode(`{"command":"create_zone","zoneKind":"stockpile","preset":"nothing","priority":"important","allow":["Silver"],"cells":[{"x":0,"z":0}]}`, 1)
	if err != nil || nothing.Preset == nil || *nothing.Preset != "nothing" || len(nothing.Allow) != 1 || nothing.Allow[0] != "Silver" {
		t.Fatalf("create_zone allow-listed stockpile shape: %v %v", nothing, err)
	}
	for _, text := range []string{
		`{"command":"create_zone"}`,
		`{"command":"create_zone","zoneKind":"growing","cells":[{"x":0,"z":0}]}`,
		`{"command":"create_zone","zoneKind":"growing","crop":"","cells":[{"x":0,"z":0}]}`,
		`{"command":"create_zone","zoneKind":"growing","crop":null,"cells":[{"x":0,"z":0}]}`,
		`{"command":"create_zone","zoneKind":"growing","crop":"Rice","cells":[]}`,
		`{"command":"create_zone","zoneKind":"growing","crop":"Rice","cells":[{"x":0}]}`,
		`{"command":"create_zone","zoneKind":"growing","crop":"Rice","cells":[{"x":0,"z":null}]}`,
		`{"command":"create_zone","zoneKind":"growing","crop":"Rice","cells":[{"x":0,"z":0}],"dryRun":false}`,
		`{"command":"create_zone","zoneKind":"stockpile","priority":"important","cells":[{"x":0,"z":0}]}`,
		`{"command":"create_zone","zoneKind":"stockpile","preset":"food","cells":[{"x":0,"z":0}]}`,
		`{"command":"create_zone","zoneKind":"stockpile","preset":"food","priority":"important","cells":[{"x":0,"z":0}],"dryRun":false}`,
		`{"command":"create_zone","zoneKind":"stockpile","preset":"nothing","priority":"important","cells":[{"x":0,"z":0}]}`,
		`{"command":"create_zone","zoneKind":"stockpile","preset":"nothing","priority":"important","allow":[],"cells":[{"x":0,"z":0}]}`,
		`{"command":"create_zone","zoneKind":"stockpile","preset":"nothing","priority":"important","allow":[""],"cells":[{"x":0,"z":0}]}`,
		`{"command":"create_zone","zoneKind":"stockpile","preset":"bogus","priority":"important","cells":[{"x":0,"z":0}]}`,
		`{"command":"create_zone","zoneKind":"bogus","cells":[{"x":0,"z":0}]}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelEditZoneShape(t *testing.T) {
	add, err := decode(`{"command":"edit_zone","zoneId":"Zone_A","operation":"add","cells":[{"x":0,"z":0},{"x":1,"z":0}]}`, 1)
	if err != nil || add.Command != "edit_zone" || add.ZoneID == nil || *add.ZoneID != "Zone_A" || add.ZoneOp == nil || *add.ZoneOp != "add" || len(add.ZoneCells) != 2 ||
		*add.ZoneCells[0].X != 0 || *add.ZoneCells[0].Z != 0 || *add.ZoneCells[1].X != 1 || *add.ZoneCells[1].Z != 0 {
		t.Fatalf("edit_zone add shape: %v %v", add, err)
	}
	remove, err := decode(`{"command":"edit_zone","zoneId":"Zone_A","operation":"remove","cells":[{"x":0,"z":0}]}`, 1)
	if err != nil || remove.ZoneOp == nil || *remove.ZoneOp != "remove" || len(remove.ZoneCells) != 1 {
		t.Fatalf("edit_zone remove shape: %v %v", remove, err)
	}
	del, err := decode(`{"command":"edit_zone","zoneId":"Zone_A","operation":"delete"}`, 1)
	if err != nil || del.ZoneOp == nil || *del.ZoneOp != "delete" || len(del.ZoneCells) != 0 {
		t.Fatalf("edit_zone delete shape: %v %v", del, err)
	}
	for _, text := range []string{
		`{"command":"edit_zone"}`,
		`{"command":"edit_zone","zoneId":"Zone_A"}`,
		`{"command":"edit_zone","zoneId":"","operation":"delete"}`,
		`{"command":"edit_zone","zoneId":null,"operation":"delete"}`,
		`{"command":"edit_zone","operation":"delete"}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":null}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":"add"}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":"add","cells":[]}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":"add","cells":[{"x":0}]}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":"remove"}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":"delete","cells":[{"x":0,"z":0}]}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":"delete","dryRun":false}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":"crop"}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":"filter"}`,
		`{"command":"edit_zone","zoneId":"Zone_A","operation":"bogus"}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelSetPopulationPolicyShape(t *testing.T) {
	ok, err := decode(`{"command":"set_population_policy","maximum":12,"foodDays":30.5}`, 1)
	if err != nil || ok.Command != "set_population_policy" || ok.Maximum == nil || *ok.Maximum != 12 || ok.FoodDays == nil || *ok.FoodDays != 30.5 {
		t.Fatalf("set_population_policy shape: %v %v", ok, err)
	}
	// Decoding bounds only the shape; the numeric range belongs to domain.
	wide, err := decode(`{"command":"set_population_policy","maximum":999,"foodDays":999}`, 1)
	if err != nil || wide.Maximum == nil || *wide.Maximum != 999 {
		t.Fatalf("out-of-range values decode here and are refused later: %v %v", wide, err)
	}
	for _, text := range []string{
		`{"command":"set_population_policy"}`,
		`{"command":"set_population_policy","maximum":12}`,
		`{"command":"set_population_policy","foodDays":30}`,
		`{"command":"set_population_policy","maximum":null,"foodDays":30}`,
		`{"command":"set_population_policy","maximum":12,"foodDays":null}`,
		`{"command":"set_population_policy","maximum":"12","foodDays":30}`,
		`{"command":"set_population_policy","maximum":12,"foodDays":"30"}`,
		`{"command":"set_population_policy","maximum":12.5,"foodDays":30}`,
		`{"command":"set_population_policy","maximum":12,"foodDays":30,"pawn":"Thing_A"}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}
