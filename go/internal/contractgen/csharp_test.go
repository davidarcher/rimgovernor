package contractgen

import (
	"bytes"
	"strings"
	"testing"
)

func TestCSharpGenerationPreservesBoundsAndWireFields(t *testing.T) {
	schema, err := ParseSchema([]byte(`{"title":"Arguments","type":"object","additionalProperties":false,"required":["placements"],"properties":{"placements":{"type":"string","x-maxUTF16Length":32768}},"$defs":{"Candidate":{"type":"object","additionalProperties":false,"required":["x","name"],"properties":{"x":{"type":"integer","x-integerToken":true,"minimum":-17,"maximum":31},"name":{"type":"string","x-maxUTF16Length":20,"x-nonBlankDotNet":true},"active":{"type":"boolean"}}},"Batch":{"type":"array","minItems":1,"maxItems":16,"items":{"$ref":"#/$defs/Candidate"}}}}`))
	if err != nil {
		t.Fatal(err)
	}
	options := CSharpOptions{Namespace: "Example.Contracts", SchemaPath: "contracts/arguments.json"}
	first, err := GenerateCSharp(schema, options)
	if err != nil {
		t.Fatal(err)
	}
	second, err := GenerateCSharp(schema, options)
	if err != nil || !bytes.Equal(first, second) {
		t.Fatalf("nondeterministic: %v", err)
	}
	for _, expected := range []string{schema.SourceSHA256, `namespace Example.Contracts`, `public static Arguments Decode(string json)`, `public IReadOnlyList<Candidate> Values { get; }`, `[JsonProperty("placements", NullValueHandling = NullValueHandling.Ignore)]`, `ReadString(token, 32768, false)`, `ReadString(token, 20, true)`, `ReadInteger(token, -17, 31)`, `array.Count < 1 || array.Count > 16`, `public bool? Active { get; }`, `DateParseHandling.None`, `DuplicatePropertyNameHandling.Error`, `internal static class ArgumentsJsonBoundary`} {
		if !bytes.Contains(first, []byte(expected)) {
			t.Errorf("missing %s", expected)
		}
	}
	if bytes.Contains(first, []byte("__BOUNDARY__")) || bytes.Contains(first, []byte("{ get; set; }")) {
		t.Fatal("unexpanded helper or mutable properties")
	}
}

func TestCSharpRejectsInvalidTargetsAndNameCollisions(t *testing.T) {
	schema, err := ParseSchema([]byte(smallSchema))
	if err != nil {
		t.Fatal(err)
	}
	for _, options := range []CSharpOptions{{Namespace: "", SchemaPath: "schema.json"}, {Namespace: "bad.namespace", SchemaPath: "schema.json"}, {Namespace: "Good", SchemaPath: "../escape.json"}, {Namespace: "Good; injected", SchemaPath: "schema.json"}} {
		if _, err := GenerateCSharp(schema, options); err == nil {
			t.Fatalf("accepted %#v", options)
		}
	}
	for _, name := range []string{"request", "decode", "getType", "equals", "toString"} {
		input := strings.ReplaceAll(smallSchema, "enabled", name)
		parsed, err := ParseSchema([]byte(input))
		if err != nil {
			t.Fatal(err)
		}
		if _, err := GenerateCSharp(parsed, CSharpOptions{Namespace: "Good", SchemaPath: "schema.json"}); err == nil {
			t.Fatalf("accepted property collision %s", name)
		}
	}
	bad := *schema
	bad.Title = "JToken"
	if _, err := GenerateCSharp(&bad, CSharpOptions{Namespace: "Good", SchemaPath: "schema.json"}); err == nil {
		t.Fatal("accepted token type collision")
	}
}

func TestCSharpNamedScalarAndReferenceWireShape(t *testing.T) {
	schema, err := ParseSchema([]byte(`{"title":"Request","type":"object","additionalProperties":false,"properties":{},"$defs":{"Alias":{"$ref":"#/$defs/Label"},"Label":{"type":"string","x-maxUTF16Length":9}}}`))
	if err != nil {
		t.Fatal(err)
	}
	data, err := GenerateCSharp(schema, CSharpOptions{Namespace: "Example", SchemaPath: "schema.json"})
	if err != nil {
		t.Fatal(err)
	}
	for _, expected := range []string{`public Label Value { get; }`, `public string Value { get; }`, `serializer.Serialize(writer, typed.Value);`, `new Alias(`, `new Label(ReadString(token, 9, false))`} {
		if !bytes.Contains(data, []byte(expected)) {
			t.Errorf("missing %s", expected)
		}
	}
}
