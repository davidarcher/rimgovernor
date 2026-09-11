// Package contractgen implements the repository's closed contract schema subset.
// Unsupported schema features fail rather than silently weakening validation.
package contractgen

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"go/token"
	"io"
	"regexp"
	"sort"
	"strings"
)

const FormatVersion = 1

// Schema is the supported JSON Schema subset. Nullable unions are not supported.
type Schema struct {
	Dialect              string             `json:"$schema,omitempty"`
	ID                   string             `json:"$id,omitempty"`
	Title                string             `json:"title,omitempty"`
	Description          string             `json:"description,omitempty"`
	Ref                  string             `json:"$ref,omitempty"`
	Definitions          map[string]*Schema `json:"$defs,omitempty"`
	Type                 string             `json:"type,omitempty"`
	Properties           map[string]*Schema `json:"properties,omitempty"`
	Required             []string           `json:"required,omitempty"`
	AdditionalProperties *bool              `json:"additionalProperties,omitempty"`
	Items                *Schema            `json:"items,omitempty"`
	MinItems             *int               `json:"minItems,omitempty"`
	MaxItems             *int               `json:"maxItems,omitempty"`
	Minimum              *int64             `json:"minimum,omitempty"`
	Maximum              *int64             `json:"maximum,omitempty"`
	IntegerToken         *bool              `json:"x-integerToken,omitempty"`
	MaxUTF16Length       *int               `json:"x-maxUTF16Length,omitempty"`
	NonBlankDotNet       *bool              `json:"x-nonBlankDotNet,omitempty"`
	SourceSHA256         string             `json:"-"`
}

// Manifest declares the only files the generator may produce, relative to -root.
type Manifest struct {
	Version   int        `json:"version"`
	Contracts []Contract `json:"contracts"`
}

type Contract struct {
	Schema    string `json:"schema"`
	GoPackage string `json:"go_package"`
	GoOutput  string `json:"go_output"`
}

func exactObject(data []byte, allowed ...string) error {
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(data, &fields); err != nil || fields == nil {
		return fmt.Errorf("expected JSON object")
	}
	for name := range fields {
		found := false
		for _, key := range allowed {
			found = found || name == key
		}
		if !found {
			return fmt.Errorf("unsupported field %q", name)
		}
	}
	return nil
}

func (schema *Schema) UnmarshalJSON(data []byte) error {
	if err := exactObject(data, "$schema", "$id", "title", "description", "$ref", "$defs", "type", "properties", "required", "additionalProperties", "items", "minItems", "maxItems", "minimum", "maximum", "x-integerToken", "x-maxUTF16Length", "x-nonBlankDotNet"); err != nil {
		return err
	}
	type plain Schema
	return json.Unmarshal(data, (*plain)(schema))
}

func (manifest *Manifest) UnmarshalJSON(data []byte) error {
	if err := exactObject(data, "version", "contracts"); err != nil {
		return err
	}
	type plain Manifest
	return json.Unmarshal(data, (*plain)(manifest))
}

func (contract *Contract) UnmarshalJSON(data []byte) error {
	if err := exactObject(data, "schema", "go_package", "go_output"); err != nil {
		return err
	}
	type plain Contract
	return json.Unmarshal(data, (*plain)(contract))
}

func ParseManifest(data []byte) (*Manifest, error) {
	var manifest Manifest
	if err := decodeInput(data, &manifest); err != nil {
		return nil, err
	}
	if manifest.Version != 1 || len(manifest.Contracts) == 0 {
		return nil, fmt.Errorf("manifest requires version 1 and at least one contract")
	}
	outputs := map[string]bool{}
	for _, entry := range manifest.Contracts {
		if !relativePath(entry.Schema) || !relativePath(entry.GoOutput) || !identifier(entry.GoPackage, false) {
			return nil, fmt.Errorf("invalid contract path or package: %q", entry.Schema)
		}
		if outputs[strings.ToLower(entry.GoOutput)] || !strings.HasSuffix(entry.GoOutput, ".go") || strings.EqualFold(entry.Schema, entry.GoOutput) {
			return nil, fmt.Errorf("duplicate or invalid output: %q", entry.GoOutput)
		}
		outputs[strings.ToLower(entry.GoOutput)] = true
	}
	for _, entry := range manifest.Contracts {
		if outputs[strings.ToLower(entry.Schema)] {
			return nil, fmt.Errorf("schema is also an output: %q", entry.Schema)
		}
	}
	return &manifest, nil
}

func relativePath(path string) bool {
	if path == "" || strings.ContainsAny(path, "\\:\x00\r\n") || strings.HasPrefix(path, "/") {
		return false
	}
	for _, part := range strings.Split(path, "/") {
		if part == "" || part == "." || part == ".." {
			return false
		}
	}
	return true
}

func ParseSchema(data []byte) (*Schema, error) {
	var schema Schema
	if err := decodeInput(data, &schema); err != nil {
		return nil, err
	}
	sum := sha256.Sum256(data)
	schema.SourceSHA256 = hex.EncodeToString(sum[:])
	if err := ValidateSchema(&schema); err != nil {
		return nil, err
	}
	return &schema, nil
}

func decodeInput(data []byte, target any) error {
	if len(data) > 1<<20 {
		return fmt.Errorf("generator input exceeds 1 MiB")
	}
	if err := validJSONText(data); err != nil {
		return err
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if err := uniqueValue(decoder, 0); err != nil {
		return err
	}
	if _, err := decoder.Token(); err != io.EOF {
		return fmt.Errorf("trailing JSON data")
	}
	decoder = json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(target); err != nil {
		return fmt.Errorf("unsupported or malformed generator input: %w", err)
	}
	return nil
}

func uniqueValue(decoder *json.Decoder, depth int) error {
	if depth > 64 {
		return fmt.Errorf("generator JSON exceeds nesting limit")
	}
	value, err := decoder.Token()
	if err != nil {
		return err
	}
	if value == nil {
		return fmt.Errorf("null generator input values are unsupported")
	}
	delim, ok := value.(json.Delim)
	if !ok {
		return nil
	}
	if delim != '{' && delim != '[' {
		return fmt.Errorf("unexpected delimiter")
	}
	keys := map[string]bool{}
	for decoder.More() {
		if delim == '{' {
			key, err := decoder.Token()
			if err != nil {
				return err
			}
			name, ok := key.(string)
			if !ok || keys[name] {
				return fmt.Errorf("duplicate or invalid object key")
			}
			keys[name] = true
		}
		if err := uniqueValue(decoder, depth+1); err != nil {
			return err
		}
	}
	_, err = decoder.Token()
	return err
}

var namePattern = regexp.MustCompile(`^[A-Za-z][A-Za-z0-9_]*$`)

func identifier(name string, exported bool) bool {
	return namePattern.MatchString(name) && !token.Lookup(name).IsKeyword() &&
		(!exported || name[0] >= 'A' && name[0] <= 'Z')
}

func fieldName(name string) string { return strings.ToUpper(name[:1]) + name[1:] }

// ValidateSchema validates constraints and resolves local definitions before any output.
func ValidateSchema(root *Schema) error {
	if root == nil || !identifier(root.Title, true) {
		return fmt.Errorf("root schema requires an exported Go identifier title")
	}
	if root.Dialect != "" && root.Dialect != "https://json-schema.org/draft/2020-12/schema" {
		return fmt.Errorf("unsupported schema dialect")
	}
	names := map[string]bool{root.Title: true}
	for name := range root.Definitions {
		if !identifier(name, true) || names[name] {
			return fmt.Errorf("invalid or duplicate definition name: %q", name)
		}
		names[name] = true
	}
	for name := range names {
		if names["Decode"+name] {
			return fmt.Errorf("type collides with generated decoder: %q", name)
		}
	}
	active := map[*Schema]bool{}
	var visit func(*Schema, bool) error
	visit = func(node *Schema, named bool) error {
		if node == nil || active[node] {
			return fmt.Errorf("missing or cyclic schema")
		}
		active[node] = true
		defer delete(active, node)
		if node != root && (node.Dialect != "" || node.ID != "" || len(node.Definitions) != 0) {
			return fmt.Errorf("schema metadata and definitions must be at root")
		}
		if node.Ref != "" {
			copy := *node
			copy.Ref, copy.Title, copy.Description, copy.SourceSHA256 = "", "", "", ""
			encoded, _ := json.Marshal(copy)
			if string(encoded) != "{}" {
				return fmt.Errorf("$ref validation siblings are unsupported")
			}
			const prefix = "#/$defs/"
			if !strings.HasPrefix(node.Ref, prefix) {
				return fmt.Errorf("only local $defs references are supported")
			}
			target := root.Definitions[strings.TrimPrefix(node.Ref, prefix)]
			return visit(target, true)
		}
		if node.Type != "object" && (len(node.Properties) != 0 || len(node.Required) != 0 || node.AdditionalProperties != nil) {
			return fmt.Errorf("object keywords on %s", node.Type)
		}
		if node.Type != "array" && (node.Items != nil || node.MinItems != nil || node.MaxItems != nil) {
			return fmt.Errorf("array keywords on %s", node.Type)
		}
		if node.Type != "integer" && (node.Minimum != nil || node.Maximum != nil || node.IntegerToken != nil) {
			return fmt.Errorf("integer keywords on %s", node.Type)
		}
		if node.Type != "string" && (node.MaxUTF16Length != nil || node.NonBlankDotNet != nil) {
			return fmt.Errorf("string keywords on %s", node.Type)
		}
		switch node.Type {
		case "object":
			if !named || node.AdditionalProperties == nil || *node.AdditionalProperties {
				return fmt.Errorf("objects must be named and declare additionalProperties:false")
			}
			required := map[string]bool{}
			for _, name := range node.Required {
				if node.Properties[name] == nil || required[name] {
					return fmt.Errorf("invalid required property: %q", name)
				}
				required[name] = true
			}
			fields := map[string]bool{}
			for _, name := range sortedKeys(node.Properties) {
				if !identifier(name, false) || fields[fieldName(name)] {
					return fmt.Errorf("invalid or colliding property name: %q", name)
				}
				fields[fieldName(name)] = true
				if err := visit(node.Properties[name], false); err != nil {
					return fmt.Errorf("property %s: %w", name, err)
				}
			}
		case "array":
			if node.MinItems == nil || node.MaxItems == nil || *node.MinItems < 0 || *node.MaxItems < *node.MinItems || *node.MaxItems > 65536 {
				return fmt.Errorf("arrays require ordered bounds within 0..65536")
			}
			return visit(node.Items, false)
		case "integer":
			if node.IntegerToken == nil || !*node.IntegerToken || node.Minimum == nil || node.Maximum == nil ||
				*node.Minimum < -2147483648 || *node.Maximum > 2147483647 || *node.Minimum > *node.Maximum {
				return fmt.Errorf("integers require x-integerToken:true and int32 bounds")
			}
		case "string":
			if node.MaxUTF16Length == nil || *node.MaxUTF16Length < 0 || *node.MaxUTF16Length > 1<<20 || node.NonBlankDotNet != nil && !*node.NonBlankDotNet {
				return fmt.Errorf("strings require bounded x-maxUTF16Length; x-nonBlankDotNet may only be true")
			}
		case "boolean":
		default:
			return fmt.Errorf("unsupported type %q (nullable schemas are deferred)", node.Type)
		}
		return nil
	}
	if err := visit(root, true); err != nil {
		return err
	}
	for _, name := range sortedKeys(root.Definitions) {
		if err := visit(root.Definitions[name], true); err != nil {
			return fmt.Errorf("definition %s: %w", name, err)
		}
	}
	return nil
}

func sortedKeys(values map[string]*Schema) []string {
	keys := make([]string, 0, len(values))
	for key := range values {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}
