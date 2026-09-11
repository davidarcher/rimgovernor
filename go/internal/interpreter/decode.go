package interpreter

import (
	"bytes"
	"encoding/json"
	"github.com/davidarcher/RimGovernor/go/internal/wire/placementpreview"
	"io"
	"unicode/utf8"
)

type wireBuilding struct {
	DefName  *string `json:"defName"`
	X        *int32  `json:"x"`
	Z        *int32  `json:"z"`
	Rotation *string `json:"rotation"`
	Stuff    *string `json:"stuff"`
}

func decode(text string, limit int) ([]wireBuilding, error) {
	if len(text) > 65536 || !utf8.ValidString(text) {
		return nil, fail(InvalidCommand, "invalid or oversized JSON response")
	}
	decoder := json.NewDecoder(bytes.NewBufferString(text))
	decoder.UseNumber()
	if err := scan(decoder, 0); err != nil {
		return nil, err
	}
	if _, err := decoder.Token(); err != io.EOF {
		return nil, fail(InvalidCommand, "trailing JSON")
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal([]byte(text), &fields); err != nil || fields == nil {
		return nil, fail(InvalidCommand, "expected command object")
	}
	var command string
	if err := json.Unmarshal(fields["command"], &command); err != nil {
		return nil, fail(InvalidCommand, "missing command")
	}
	if command != "build" {
		return nil, fail(UnsupportedCommand, "only building proposals are supported")
	}
	if len(fields) != 2 || fields["buildings"] == nil {
		return nil, fail(InvalidCommand, "unexpected command fields")
	}
	// Reuse the native placement wire contract for scalar Unicode, lexical
	// integers, required fields and definition-text bounds before domain work.
	if _, err := placementpreview.DecodePlacementBatch(fields["buildings"]); err != nil {
		return nil, fail(InvalidCommand, "invalid placement wire contract")
	}
	var buildings []map[string]json.RawMessage
	if err := json.Unmarshal(fields["buildings"], &buildings); err != nil || len(buildings) == 0 || len(buildings) > limit {
		return nil, fail(InvalidCommand, "expected bounded nonempty building list")
	}
	result := make([]wireBuilding, 0, len(buildings))
	for _, fields := range buildings {
		if len(fields) != 5 {
			return nil, fail(InvalidCommand, "building requires five exact fields")
		}
		for _, key := range []string{"defName", "x", "z", "rotation", "stuff"} {
			if fields[key] == nil || bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
				return nil, fail(InvalidCommand, "missing or null building field")
			}
		}
		raw, _ := json.Marshal(fields)
		var b wireBuilding
		if err := json.Unmarshal(raw, &b); err != nil {
			return nil, fail(InvalidCommand, "invalid building field type")
		}
		result = append(result, b)
	}
	return result, nil
}

// Generic JSON token inspection is confined to this external text boundary.
func scan(decoder *json.Decoder, depth int) error {
	if depth > 16 {
		return fail(InvalidCommand, "JSON nesting exceeds limit")
	}
	token, err := decoder.Token()
	if err != nil {
		return fail(InvalidCommand, "malformed JSON")
	}
	delimiter, container := token.(json.Delim)
	if !container {
		return nil
	}
	seen := map[string]bool{}
	for decoder.More() {
		if delimiter == '{' {
			key, err := decoder.Token()
			if err != nil {
				return fail(InvalidCommand, "malformed JSON key")
			}
			name, ok := key.(string)
			if !ok || seen[name] {
				return fail(InvalidCommand, "duplicate JSON key")
			}
			seen[name] = true
		}
		if err := scan(decoder, depth+1); err != nil {
			return err
		}
	}
	if _, err := decoder.Token(); err != nil {
		return fail(InvalidCommand, "malformed JSON container")
	}
	return nil
}
