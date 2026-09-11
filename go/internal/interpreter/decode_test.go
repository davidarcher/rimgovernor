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
	buildings, err := decode(text, 1)
	if err != nil || len(buildings) != 1 || *buildings[0].X != -2147483648 || *buildings[0].Z != 2147483647 {
		t.Fatalf("integer bounds: %v %v", buildings, err)
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
	buildings, err := decode(text, 1)
	if err != nil || *buildings[0].DefName != "Modded🏠" {
		t.Fatalf("Unicode text: %v %v", buildings, err)
	}
	text = strings.Replace(valid, `Wall`, strings.Repeat("a", 240), 1)
	if _, err := decode(text, 1); err != nil {
		t.Fatalf("model decoder retained native UTF16 limit: %v", err)
	}
}
