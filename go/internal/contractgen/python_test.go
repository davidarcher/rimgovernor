package contractgen

import (
	"bytes"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

func contractPython(t *testing.T) string {
	t.Helper()
	if configured := os.Getenv("RIMGOVERNOR_CONTRACT_PYTHON"); configured != "" {
		return configured
	}
	for _, name := range []string{"python3", "python"} {
		if path, err := exec.LookPath(name); err == nil {
			return path
		}
	}
	t.Skip("Python interpreter unavailable; generated Python acceptance requires scripts/check_placement_requests.py")
	return ""
}

func TestPythonPlacementFixtureDecoding(t *testing.T) {
	data, err := os.ReadFile(filepath.Join("..", "..", "..", "contracts", "schemas", "placement-previews-request.v1.schema.json"))
	if err != nil {
		t.Fatal(err)
	}
	schema, err := ParseSchema(data)
	if err != nil {
		t.Fatal(err)
	}
	options := PythonOptions{SchemaPath: "contracts/schemas/placement-previews-request.v1.schema.json"}
	generated, err := GeneratePython(schema, options)
	if err != nil {
		t.Fatal(err)
	}
	again, err := GeneratePython(schema, options)
	if err != nil || !bytes.Equal(generated, again) {
		t.Fatal("Python generation is nondeterministic")
	}
	if !bytes.Contains(generated, []byte(schema.SourceSHA256)) || !bytes.Contains(generated, []byte(options.SchemaPath)) {
		t.Fatal("generated provenance missing")
	}
	path := filepath.Join(t.TempDir(), "placement_preview_requests.py")
	if err := os.WriteFile(path, generated, 0600); err != nil {
		t.Fatal(err)
	}
	command := exec.Command(contractPython(t), filepath.Join("..", "..", "..", "scripts", "check_placement_requests.py"), "--module", path)
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("shared Python structural cases: %v\n%s", err, output)
	}
}

func TestPythonOptionalAndTypedBoundaries(t *testing.T) {
	schema, err := ParseSchema([]byte(`{
        "title":"Example","type":"object","additionalProperties":false,"required":["flag"],
        "properties":{"flag":{"type":"boolean"},"note":{"type":"string","x-maxUTF16Length":10},
        "rows":{"$ref":"#/$defs/Numbers"}},
        "$defs":{"Numbers":{"type":"array","minItems":0,"maxItems":2,"items":{"type":"integer","minimum":-2,"maximum":2,"x-integerToken":true}}}}`))
	if err != nil {
		t.Fatal(err)
	}
	generated, err := GeneratePython(schema, PythonOptions{SchemaPath: "test/example.json"})
	if err != nil {
		t.Fatal(err)
	}
	directory := t.TempDir()
	if err := os.WriteFile(filepath.Join(directory, "generated.py"), generated, 0600); err != nil {
		t.Fatal(err)
	}
	code := `import json
import generated as g
a = g.decode_example('{"flag":true}')
assert isinstance(a, g.Example) and isinstance(a.note, g.Missing)
assert g.to_wire(a) == {"flag": True}
assert g.decode_example(json.dumps(g.to_wire(a))) == a
b = g.decode_example('{"flag":false,"note":"ok","rows":[-2,2]}')
assert b.flag is False and b.note == 'ok' and b.rows == [-2,2]
for invalid in ['{"flag":1}', '{"flag":null}', '{"flag":true,"note":null}', '{"flag":true,"rows":[true]}', '{"flag":true,"rows":[1.0]}', '{"flag":true,"rows":[3]}', '{"flag":true,"unknown":0}', '{"flag":true,"note":"\\ud800"}', '{"flag":true} null', '['*65+'0'+']'*65, ' '*1048576+'{}']:
    try:
        g.decode_example(invalid)
    except ValueError:
        pass
    else:
        raise AssertionError('accepted invalid input: '+invalid[:80])
assert g.decode_numbers('[1,2]') == [1,2]
`
	command := exec.Command(contractPython(t), "-c", code)
	command.Dir = directory
	if output, err := command.CombinedOutput(); err != nil {
		t.Fatalf("Python optional/type semantics: %v\n%s", err, output)
	}
}

func TestPythonRejectsUnsupportedNamesAndSchema(t *testing.T) {
	for _, name := range []string{"class", "None", "json"} {
		schema := &Schema{Title: "Example", Type: "object", AdditionalProperties: new(false), Properties: map[string]*Schema{name: {Type: "boolean"}}}
		if _, err := GeneratePython(schema, PythonOptions{SchemaPath: "schema.json"}); err == nil {
			t.Fatalf("Python reserved property %s accepted", name)
		}
	}
	if _, err := GeneratePython(&Schema{Title: "Example", Type: "number"}, PythonOptions{SchemaPath: "schema.json"}); err == nil {
		t.Fatal("unsupported schema type accepted")
	}
	schema := &Schema{Title: "Example", Type: "boolean"}
	if _, err := GeneratePython(schema, PythonOptions{SchemaPath: "schema.json\nprint('injected')"}); err == nil {
		t.Fatal("provenance newline accepted")
	}
	schema.Definitions = map[string]*Schema{"ABC": {Type: "boolean"}, "Abc": {Type: "boolean"}}
	if _, err := GeneratePython(schema, PythonOptions{SchemaPath: "schema.json"}); err == nil || !strings.Contains(err.Error(), "collision") {
		t.Fatalf("decoder naming collision accepted: %v", err)
	}
}
