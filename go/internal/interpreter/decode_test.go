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

func TestModelSetExpeditionPolicyShape(t *testing.T) {
	ok, err := decode(`{"command":"set_expedition_policy","maximumTravelDays":9.5,"keepHomeDoctor":false}`, 1)
	if err != nil || ok.Command != "set_expedition_policy" || ok.ExpeditionPolicy == nil {
		t.Fatalf("set_expedition_policy shape: %v %v", ok, err)
	}
	if ok.ExpeditionPolicy.MaximumTravelDays == nil || *ok.ExpeditionPolicy.MaximumTravelDays != 9.5 ||
		ok.ExpeditionPolicy.KeepHomeDoctor == nil || *ok.ExpeditionPolicy.KeepHomeDoctor {
		t.Fatalf("supplied limits: %v", *ok.ExpeditionPolicy)
	}
	// A partial request leaves every limit it does not name absent, which is
	// what lets the store preserve the established value.
	if ok.ExpeditionPolicy.MinimumHomeColonists != nil || ok.ExpeditionPolicy.RequireReturnStorage != nil {
		t.Fatalf("unrequested limits must stay absent: %v", *ok.ExpeditionPolicy)
	}
	// Every limit may be named at once.
	whole, err := decode(`{"command":"set_expedition_policy","minimumHomeColonists":6,"minimumHomeFoodDays":12.25,"travelFoodMarginDays":2.5,"maximumTravelDays":11.5,"maximumCaravans":5,"minimumGoodwill":20,"minimumDestinationTemperature":-30.5,"maximumDestinationTemperature":45.5,"keepHomeDoctor":false,"requireReturnStorage":false}`, 1)
	if err != nil || whole.ExpeditionPolicy == nil || whole.ExpeditionPolicy.MinimumHomeColonists == nil || *whole.ExpeditionPolicy.MinimumHomeColonists != 6 {
		t.Fatalf("whole policy shape: %v %v", whole, err)
	}
	// Decoding bounds only the shape; the numeric ranges belong to domain.
	wide, err := decode(`{"command":"set_expedition_policy","maximumCaravans":999}`, 1)
	if err != nil || wide.ExpeditionPolicy.MaximumCaravans == nil || *wide.ExpeditionPolicy.MaximumCaravans != 999 {
		t.Fatalf("out-of-range values decode here and are refused later: %v %v", wide, err)
	}
	for _, text := range []string{
		`{"command":"set_expedition_policy"}`,
		`{"command":"set_expedition_policy","maximumTravelDays":null}`,
		`{"command":"set_expedition_policy","maximumTravelDays":"9"}`,
		`{"command":"set_expedition_policy","minimumHomeColonists":6.5}`,
		`{"command":"set_expedition_policy","keepHomeDoctor":"false"}`,
		`{"command":"set_expedition_policy","maximumTravelDays":9,"pawn":"Thing_A"}`,
		`{"command":"set_expedition_policy","maximumTravelDay":9}`,
		`{"command":"set_expedition_policy","maximum":12,"foodDays":30}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelSetPopulationDecisionShape(t *testing.T) {
	ok, err := decode(`{"command":"set_population_decision","pawn":"Thing_A","decision":"rescue"}`, 1)
	if err != nil || ok.Command != "set_population_decision" || ok.Pawn == nil || *ok.Pawn != "Thing_A" || ok.Decision == nil || *ok.Decision != "rescue" {
		t.Fatalf("set_population_decision shape: %v %v", ok, err)
	}
	// Decoding bounds only the shape: the decision vocabulary belongs to
	// domain and the pawn is bounded against facts by the interpreter.
	unknown, err := decode(`{"command":"set_population_decision","pawn":"Thing_A","decision":"release"}`, 1)
	if err != nil || unknown.Decision == nil || *unknown.Decision != "release" {
		t.Fatalf("unsupported decisions decode here and are refused later: %v %v", unknown, err)
	}
	for _, text := range []string{
		`{"command":"set_population_decision"}`,
		`{"command":"set_population_decision","pawn":"Thing_A"}`,
		`{"command":"set_population_decision","decision":"rescue"}`,
		`{"command":"set_population_decision","pawn":null,"decision":"rescue"}`,
		`{"command":"set_population_decision","pawn":"Thing_A","decision":null}`,
		`{"command":"set_population_decision","pawn":"","decision":"rescue"}`,
		`{"command":"set_population_decision","pawn":"Thing_A","decision":""}`,
		`{"command":"set_population_decision","pawn":"Thing_A","decision":1}`,
		`{"command":"set_population_decision","pawn":"Thing_A","decision":"rescue","maximum":12}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}

func TestModelResourcePolicyShapes(t *testing.T) {
	spending, err := decode(`{"command":"modify_resource_policy","resource":"Steel","spending":"defense_only"}`, 1)
	if err != nil || spending.Command != "modify_resource_policy" || spending.Resource == nil || *spending.Resource != "Steel" ||
		spending.Spending == nil || *spending.Spending != "defense_only" || spending.Reserve != nil {
		t.Fatalf("modify_resource_policy shape: %v %v", spending, err)
	}
	reserve, err := decode(`{"command":"set_resource_reserve","resource":"Steel","reserve":0}`, 1)
	if err != nil || reserve.Command != "set_resource_reserve" || reserve.Resource == nil || *reserve.Resource != "Steel" ||
		reserve.Reserve == nil || *reserve.Reserve != 0 || reserve.Spending != nil {
		t.Fatalf("set_resource_reserve shape: %v %v", reserve, err)
	}
	// Decoding bounds only the shape: the restriction vocabulary and the
	// reserve range belong to domain, and the resource is bounded against facts
	// by the interpreter.
	unknown, err := decode(`{"command":"modify_resource_policy","resource":"Steel","spending":"hoard"}`, 1)
	if err != nil || unknown.Spending == nil || *unknown.Spending != "hoard" {
		t.Fatalf("unsupported restrictions decode here and are refused later: %v %v", unknown, err)
	}
	for _, text := range []string{
		`{"command":"modify_resource_policy"}`,
		`{"command":"modify_resource_policy","resource":"Steel"}`,
		`{"command":"modify_resource_policy","spending":"stop"}`,
		`{"command":"modify_resource_policy","resource":null,"spending":"stop"}`,
		`{"command":"modify_resource_policy","resource":"Steel","spending":null}`,
		`{"command":"modify_resource_policy","resource":"","spending":"stop"}`,
		`{"command":"modify_resource_policy","resource":"Steel","spending":""}`,
		`{"command":"modify_resource_policy","resource":"Steel","spending":1}`,
		// Neither command may set both halves: each is its own contract.
		`{"command":"modify_resource_policy","resource":"Steel","spending":"stop","reserve":5}`,
		`{"command":"set_resource_reserve"}`,
		`{"command":"set_resource_reserve","resource":"Steel"}`,
		`{"command":"set_resource_reserve","reserve":5}`,
		`{"command":"set_resource_reserve","resource":"Steel","reserve":null}`,
		`{"command":"set_resource_reserve","resource":"","reserve":5}`,
		`{"command":"set_resource_reserve","resource":"Steel","reserve":"5"}`,
		`{"command":"set_resource_reserve","resource":"Steel","reserve":1.5}`,
		`{"command":"set_resource_reserve","resource":"Steel","reserve":5,"spending":"stop"}`,
	} {
		t.Run(text, func(t *testing.T) {
			_, err := decode(text, 1)
			assertKind(t, err, InvalidCommand)
		})
	}
}
