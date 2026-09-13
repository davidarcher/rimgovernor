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
