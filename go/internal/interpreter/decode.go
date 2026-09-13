package interpreter

import (
	"bytes"
	"encoding/json"
	"io"
	"unicode/utf8"
)

// modelBuilding is an untrusted proposal, separate from native transport types.
// Pointers distinguish required fields from absent or null values.
type modelBuilding struct {
	DefName  *string `json:"defName"`
	X        *int32  `json:"x"`
	Z        *int32  `json:"z"`
	Rotation *string `json:"rotation"`
	Stuff    *string `json:"stuff"`
}

// modelCargo is one untrusted requested caravan cargo line.
type modelCargo struct {
	Definition *string `json:"defName"`
	Count      *uint64 `json:"count"`
}

// modelCommand is the untrusted decoded shape of exactly one supported
// command. Only the fields matching Command are populated.
type modelCommand struct {
	Command   string
	Buildings []modelBuilding
	Project   *string
	// First/Second hold tend's doctor/patient, rescue's rescuer/patient, or
	// draft's sole pawn, in that role order; the field names are shared
	// since these are the same one/two-distinct-pawn shapes.
	First, Second   *string
	Crew            []string
	Cargo           []modelCargo
	DestinationTile *int32
}

func decode(text string, limit int) (modelCommand, error) {
	if len(text) > 65536 || !utf8.ValidString(text) {
		return modelCommand{}, fail(InvalidCommand, "invalid or oversized JSON response")
	}
	decoder := json.NewDecoder(bytes.NewBufferString(text))
	decoder.UseNumber()
	if err := scan(decoder, 0); err != nil {
		return modelCommand{}, err
	}
	if _, err := decoder.Token(); err != io.EOF {
		return modelCommand{}, fail(InvalidCommand, "trailing JSON")
	}
	var fields map[string]json.RawMessage
	if err := json.Unmarshal([]byte(text), &fields); err != nil || fields == nil {
		return modelCommand{}, fail(InvalidCommand, "expected command object")
	}
	var command *string
	if err := json.Unmarshal(fields["command"], &command); err != nil || command == nil {
		return modelCommand{}, fail(InvalidCommand, "missing command")
	}
	switch *command {
	case "build":
		return decodeBuild(fields, limit)
	case "research":
		return decodeResearch(fields)
	case "tend":
		return decodeTwoPawns(fields, "tend", "doctor", "patient")
	case "rescue":
		return decodeTwoPawns(fields, "rescue", "rescuer", "patient")
	case "draft":
		return decodeOnePawn(fields, "draft", "pawn")
	case "caravan":
		return decodeCaravan(fields)
	default:
		return modelCommand{}, fail(UnsupportedCommand, "only building, research, tend, rescue, draft and caravan proposals are supported")
	}
}

func decodeBuild(fields map[string]json.RawMessage, limit int) (modelCommand, error) {
	if len(fields) != 2 || fields["buildings"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var buildings []json.RawMessage
	if err := json.Unmarshal(fields["buildings"], &buildings); err != nil || len(buildings) == 0 || len(buildings) > limit {
		return modelCommand{}, fail(InvalidCommand, "expected bounded nonempty building list")
	}
	result := make([]modelBuilding, 0, len(buildings))
	for _, raw := range buildings {
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(raw, &fields); err != nil {
			return modelCommand{}, fail(InvalidCommand, "expected building object")
		}
		if len(fields) != 5 {
			return modelCommand{}, fail(InvalidCommand, "building requires five exact fields")
		}
		for _, key := range []string{"defName", "x", "z", "rotation", "stuff"} {
			if fields[key] == nil || bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
				return modelCommand{}, fail(InvalidCommand, "missing or null building field")
			}
		}
		var b modelBuilding
		if err := json.Unmarshal(raw, &b); err != nil {
			return modelCommand{}, fail(InvalidCommand, "invalid building field type")
		}
		result = append(result, b)
	}
	return modelCommand{Command: "build", Buildings: result}, nil
}

func decodeResearch(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 2 || fields["project"] == nil || bytes.Equal(bytes.TrimSpace(fields["project"]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var project string
	if err := json.Unmarshal(fields["project"], &project); err != nil || project == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid project field")
	}
	return modelCommand{Command: "research", Project: &project}, nil
}

func decodeTwoPawns(fields map[string]json.RawMessage, command, firstKey, secondKey string) (modelCommand, error) {
	if len(fields) != 3 || fields[firstKey] == nil || fields[secondKey] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	decodeField := func(key string) (*string, error) {
		if bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
			return nil, fail(InvalidCommand, "missing "+key)
		}
		var value string
		if err := json.Unmarshal(fields[key], &value); err != nil || value == "" {
			return nil, fail(InvalidCommand, "invalid "+key+" field")
		}
		return &value, nil
	}
	first, err := decodeField(firstKey)
	if err != nil {
		return modelCommand{}, err
	}
	second, err := decodeField(secondKey)
	if err != nil {
		return modelCommand{}, err
	}
	return modelCommand{Command: command, First: first, Second: second}, nil
}

func decodeOnePawn(fields map[string]json.RawMessage, command, key string) (modelCommand, error) {
	if len(fields) != 2 || fields[key] == nil || bytes.Equal(bytes.TrimSpace(fields[key]), []byte("null")) {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var value string
	if err := json.Unmarshal(fields[key], &value); err != nil || value == "" {
		return modelCommand{}, fail(InvalidCommand, "invalid "+key+" field")
	}
	return modelCommand{Command: command, First: &value}, nil
}

func decodeCaravan(fields map[string]json.RawMessage) (modelCommand, error) {
	if len(fields) != 4 || fields["crew"] == nil || fields["cargo"] == nil || fields["destinationTile"] == nil {
		return modelCommand{}, fail(InvalidCommand, "unexpected command fields")
	}
	var crew []string
	if err := json.Unmarshal(fields["crew"], &crew); err != nil || len(crew) == 0 || len(crew) > 64 {
		return modelCommand{}, fail(InvalidCommand, "expected bounded nonempty crew list")
	}
	for _, pawn := range crew {
		if pawn == "" {
			return modelCommand{}, fail(InvalidCommand, "empty crew pawn")
		}
	}
	var rawCargo []json.RawMessage
	if err := json.Unmarshal(fields["cargo"], &rawCargo); err != nil || len(rawCargo) == 0 || len(rawCargo) > 256 {
		return modelCommand{}, fail(InvalidCommand, "expected bounded nonempty cargo list")
	}
	cargo := make([]modelCargo, 0, len(rawCargo))
	for _, raw := range rawCargo {
		var itemFields map[string]json.RawMessage
		if err := json.Unmarshal(raw, &itemFields); err != nil {
			return modelCommand{}, fail(InvalidCommand, "expected cargo object")
		}
		if len(itemFields) != 2 {
			return modelCommand{}, fail(InvalidCommand, "cargo requires two exact fields")
		}
		for _, key := range []string{"defName", "count"} {
			if itemFields[key] == nil || bytes.Equal(bytes.TrimSpace(itemFields[key]), []byte("null")) {
				return modelCommand{}, fail(InvalidCommand, "missing or null cargo field")
			}
		}
		var item modelCargo
		if err := json.Unmarshal(raw, &item); err != nil || *item.Definition == "" || *item.Count == 0 {
			return modelCommand{}, fail(InvalidCommand, "invalid cargo field")
		}
		cargo = append(cargo, item)
	}
	var tile int32
	if err := json.Unmarshal(fields["destinationTile"], &tile); err != nil || tile < 0 {
		return modelCommand{}, fail(InvalidCommand, "invalid destinationTile field")
	}
	return modelCommand{Command: "caravan", Crew: crew, Cargo: cargo, DestinationTile: &tile}, nil
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
