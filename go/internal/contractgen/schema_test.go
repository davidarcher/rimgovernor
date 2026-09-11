package contractgen

import (
	"bytes"
	"fmt"
	"strings"
	"testing"
)

const smallSchema = `{"title":"Request","type":"object","additionalProperties":false,"required":["enabled"],"properties":{"enabled":{"type":"boolean"}}}`

func TestSchemaRejectsUnsupportedAndAmbiguousInput(t *testing.T) {
	for _, data := range []string{
		strings.Replace(smallSchema, `"title":"Request"`, `"title":"Request","title":"Other"`, 1),
		strings.Replace(smallSchema, `"type":"object"`, `"TYPE":"object"`, 1),
		strings.Replace(smallSchema, `"type":"boolean"`, `"type":["boolean","null"]`, 1),
		strings.Replace(smallSchema, `"type":"boolean"`, `"type":"boolean","default":false`, 1),
		strings.Replace(smallSchema, `"type":"boolean"`, `"type":"boolean","properties":{}`, 1),
		strings.Replace(smallSchema, `"type":"boolean"`, `"type":"boolean","required":[]`, 1),
		strings.Replace(smallSchema, `"additionalProperties":false`, `"additionalProperties":true`, 1),
		strings.Replace(smallSchema, `"required":["enabled"]`, `"required":["absent"]`, 1),
		strings.Replace(smallSchema, `"required":["enabled"]`, `"required":null`, 1),
		strings.Replace(smallSchema, `"type":"boolean"`, `"$ref":"https://example.invalid/schema"`, 1),
		strings.Replace(smallSchema, `"title":"Request"`, `"title":"\ud800"`, 1),
		smallSchema + `{}`,
	} {
		if _, err := ParseSchema([]byte(data)); err == nil {
			t.Fatalf("accepted %s", data)
		}
	}
}

func TestSharedReferenceDAGAndCycle(t *testing.T) {
	definitions := []string{`"Leaf":{"type":"boolean"}`}
	previous := "Leaf"
	for index := 0; index < 28; index++ {
		name := fmt.Sprintf("Level%d", index)
		definitions = append(definitions, fmt.Sprintf(`%q:{"type":"object","properties":{"left":{"$ref":"#/$defs/%s"},"right":{"$ref":"#/$defs/%s"}},"required":["left","right"],"additionalProperties":false}`, name, previous, previous))
		previous = name
	}
	document := `{"title":"Root","type":"object","properties":{"value":{"$ref":"#/$defs/Level27"}},"required":["value"],"additionalProperties":false,"$defs":{` + strings.Join(definitions, ",") + `}}`
	if _, err := ParseSchema([]byte(document)); err != nil {
		t.Fatal(err)
	}
	cycle := strings.Replace(document, `"Leaf":{"type":"boolean"}`, `"Leaf":{"$ref":"#/$defs/Level27"}`, 1)
	if _, err := ParseSchema([]byte(cycle)); err == nil {
		t.Fatal("cyclic graph accepted")
	}
}

func TestGenerationIsDeterministic(t *testing.T) {
	schema, err := ParseSchema([]byte(smallSchema))
	if err != nil {
		t.Fatal(err)
	}
	options := GoOptions{Package: "request", SchemaPath: "contracts/request.json"}
	first, err := GenerateGo(schema, options)
	if err != nil {
		t.Fatal(err)
	}
	second, err := GenerateGo(schema, options)
	if err != nil || !bytes.Equal(first, second) {
		t.Fatalf("non-deterministic: %v", err)
	}
	if !bytes.Contains(first, []byte(schema.SourceSHA256)) || !bytes.Contains(first, []byte("func DecodeRequest")) {
		t.Fatal("missing provenance or decoder")
	}
}

func TestManifestRejectsCaseAliasesAndPathEscapes(t *testing.T) {
	for _, data := range []string{
		`{"version":1,"contracts":[{"schema":"schema.json","Go_Package":"wire","go_output":"out.go"}]}`,
		`{"version":1,"contracts":[{"schema":"../schema.json","go_package":"wire","go_output":"out.go"}]}`,
		`{"version":1,"contracts":[{"schema":"schema.json","go_package":"wire","go_output":"out.go"},{"schema":"other.json","go_package":"wire","go_output":"OUT.go"}]}`,
	} {
		if _, err := ParseManifest([]byte(data)); err == nil {
			t.Fatalf("accepted %s", data)
		}
	}
}
