package contractgen

import (
	"encoding/json"
	"os"
	"os/exec"
	"testing"
)

func TestPythonCurrentReplyCases(t *testing.T) {
	cmd := exec.Command(contractPython(t), "../../../scripts/check_placement_responses.py")
	if output, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("Python current reply cases: %v\n%s", err, output)
	}
}

func TestCurrentReplySchemaRoundTrip(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/schemas/placement-previews-response.v2.schema.json")
	if err != nil {
		t.Fatal(err)
	}
	s, err := ParseSchema(data)
	if err != nil {
		t.Fatal(err)
	}
	encoded, err := json.Marshal(s)
	if err != nil {
		t.Fatal(err)
	}
	round, err := ParseSchema(encoded)
	if err != nil {
		t.Fatal(err)
	}
	if !round.Definitions["PreviewMaterialStock"].Properties["available"].Nullable {
		t.Fatal("nullable type lost")
	}
	if len(round.OneOf) != 2 || len(round.Definitions["PreviewRotation"].Properties["rotation"].Enum) != 4 {
		t.Fatal("closed variants lost")
	}
}

func TestReplySchemaRejectsAmbiguousVariants(t *testing.T) {
	data, err := os.ReadFile("../../../contracts/schemas/placement-previews-response.v2.schema.json")
	if err != nil {
		t.Fatal(err)
	}
	for name, mutate := range map[string]func(*Schema){
		"same tag":         func(s *Schema) { *s.Definitions["PreviewFailure"].Properties["success"].Const = true },
		"optional tag":     func(s *Schema) { s.Definitions["PreviewFailure"].Required = []string{"error"} },
		"untagged":         func(s *Schema) { s.Definitions["PreviewFailure"].Properties["success"].Const = nil },
		"inline branch":    func(s *Schema) { s.OneOf[0] = s.Definitions["PreviewBatch"] },
		"extra union type": func(s *Schema) { s.Type = "object" },
		"empty enum":       func(s *Schema) { s.Definitions["PreviewRotation"].Properties["rotation"].Enum = []string{} },
		"duplicate enum": func(s *Schema) {
			s.Definitions["PreviewRotation"].Properties["rotation"].Enum = []string{"north", "north"}
		},
		"nullable bool": func(s *Schema) { s.Definitions["PreviewFailure"].Properties["success"].Nullable = true },
	} {
		t.Run(name, func(t *testing.T) {
			s, err := ParseSchema(data)
			if err != nil {
				t.Fatal(err)
			}
			mutate(s)
			if err = ValidateSchema(s); err == nil {
				t.Fatal("invalid schema accepted")
			}
		})
	}
	for _, raw := range []string{`{"title":"Bad","type":["string","null"]}`, `{"title":"Bad","type":["integer","null","integer"]}`, `{"title":"Bad","type":"integer","const":true}`} {
		if _, err := ParseSchema([]byte(raw)); err == nil {
			t.Fatal("unsupported schema accepted")
		}
	}
}
