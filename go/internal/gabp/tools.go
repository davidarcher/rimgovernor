package gabp

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
)

// Tool is one tools/list descriptor with its input schema normalised to a
// JSON schema object.
type Tool struct {
	Name         string          `json:"name"`
	Title        string          `json:"title,omitempty"`
	Description  string          `json:"description,omitempty"`
	InputSchema  json.RawMessage `json:"inputSchema,omitempty"`
	OutputSchema json.RawMessage `json:"outputSchema,omitempty"`
	Tags         []string        `json:"tags,omitempty"`
}

// toolParameter is the Lib.GAB parameter list form some servers send in
// place of inputSchema.
type toolParameter struct {
	Name         string `json:"name"`
	Type         string `json:"type"`
	Description  string `json:"description,omitempty"`
	Required     bool   `json:"required"`
	DefaultValue any    `json:"defaultValue,omitempty"`
}

// ListTools calls tools/list ({} -> {"tools": [...]}).
func (c *Conn) ListTools(ctx context.Context) ([]Tool, error) {
	raw, err := c.Call(ctx, MethodToolsList, struct{}{})
	if err != nil {
		return nil, err
	}
	var res struct {
		Tools []struct {
			Tool
			Parameters []toolParameter `json:"parameters"`
		} `json:"tools"`
	}
	if err := json.Unmarshal(raw, &res); err != nil {
		return nil, fmt.Errorf("gabp: tools/list: %w", err)
	}
	tools := make([]Tool, len(res.Tools))
	for i, t := range res.Tools {
		tools[i] = t.Tool
		if isEmptyJSON(t.InputSchema) {
			tools[i].InputSchema = schemaFromParameters(t.Parameters)
		}
	}
	return tools, nil
}

func isEmptyJSON(raw json.RawMessage) bool {
	s := string(raw)
	return s == "" || s == "null" || s == "{}"
}

func schemaFromParameters(params []toolParameter) json.RawMessage {
	props := map[string]any{}
	required := []string{}
	for _, p := range params {
		prop := map[string]any{"type": jsonSchemaType(p.Type)}
		if p.Description != "" {
			prop["description"] = p.Description
		}
		if p.DefaultValue != nil {
			prop["default"] = p.DefaultValue
		}
		props[p.Name] = prop
		if p.Required {
			required = append(required, p.Name)
		}
	}
	schema := map[string]any{"type": "object", "properties": props}
	if len(required) > 0 {
		schema["required"] = required
	}
	out, _ := json.Marshal(schema)
	return out
}

// jsonSchemaType maps C# type names to JSON schema types.
func jsonSchemaType(t string) string {
	switch t {
	case "Int32", "Int64", "int", "long":
		return "integer"
	case "Single", "Double", "float", "double":
		return "number"
	case "Boolean", "bool":
		return "boolean"
	}
	return "string"
}

// CallTool calls tools/call with the arguments object under "parameters",
// the key the GABP host (Lib.GAB) reads first.
//
// A server error response is a tool-level refusal, not a transport
// failure: result is the error object's JSON ({code,message,data}) and
// isError is true. A success result carrying "isError": true is flagged
// too. err is set only when no answer arrived (disconnect, ctx, encoding).
func (c *Conn) CallTool(ctx context.Context, name string, args json.RawMessage) (result json.RawMessage, isError bool, err error) {
	if isEmptyJSON(args) {
		args = json.RawMessage("{}")
	}
	params := struct {
		Name       string          `json:"name"`
		Parameters json.RawMessage `json:"parameters"`
	}{name, args}
	raw, err := c.Call(ctx, MethodToolsCall, params)
	var remote *RemoteError
	if errors.As(err, &remote) {
		out, _ := json.Marshal(remote)
		return out, true, nil
	}
	if err != nil {
		return nil, false, err
	}
	var flag struct {
		IsError bool `json:"isError"`
	}
	_ = json.Unmarshal(raw, &flag) // non-object results carry no flag
	return raw, flag.IsError, nil
}

// Subscribe calls events/subscribe ({"channels": [...]}); events arrive on
// Options.OnEvent.
func (c *Conn) Subscribe(ctx context.Context, channels ...string) error {
	_, err := c.Call(ctx, MethodEventsSubscribe, map[string]any{"channels": channels})
	return err
}
