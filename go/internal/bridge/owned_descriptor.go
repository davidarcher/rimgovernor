package bridge

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
)

// validateOwnedStringInput recognizes the SDK's raw-object CLR binder metadata
// for the canonical placement string wrapper. Generated outer and inner decoders are
// authoritative for values; this exception never applies to other native tools.
func validateOwnedStringInput(detail json.RawMessage, field string) error {
	if field != "placements" {
		return fmt.Errorf("%w: unreviewed string wrapper", ErrContract)
	}
	var envelope struct {
		InputSchema json.RawMessage `json:"inputSchema"`
	}
	if err := json.Unmarshal(detail, &envelope); err != nil {
		return fmt.Errorf("%w: descriptor: %w", ErrContract, err)
	}
	var schema struct {
		Type                 string                     `json:"type"`
		Properties           map[string]json.RawMessage `json:"properties"`
		AdditionalProperties *bool                      `json:"additionalProperties"`
		Required             []string                   `json:"required"`
		Description          string                     `json:"description"`
		Title                string                     `json:"title"`
	}
	if err := decodeDescriptor(envelope.InputSchema, &schema); err != nil {
		return err
	}
	if schema.Type != "object" || schema.AdditionalProperties == nil || *schema.AdditionalProperties || len(schema.Properties) != 1 {
		return fmt.Errorf("%w: unexpected owned wrapper schema", ErrContract)
	}
	property, ok := schema.Properties[field]
	if !ok {
		return fmt.Errorf("%w: wrapper missing", ErrContract)
	}
	var p struct {
		Type        string `json:"type"`
		Description string `json:"description"`
		Title       string `json:"title"`
	}
	if err := decodeDescriptor(property, &p); err != nil {
		return err
	}
	if p.Type != "object" && p.Type != "string" {
		return fmt.Errorf("%w: wrapper type changed", ErrContract)
	}
	if len(schema.Required) > 1 || (len(schema.Required) == 1 && schema.Required[0] != field) {
		return fmt.Errorf("%w: wrapper required fields changed", ErrContract)
	}
	return nil
}
func decodeDescriptor(raw json.RawMessage, target any) error {
	d := json.NewDecoder(bytes.NewReader(raw))
	d.DisallowUnknownFields()
	if err := d.Decode(target); err != nil {
		return fmt.Errorf("%w: owned descriptor: %w", ErrContract, err)
	}
	if err := d.Decode(new(json.RawMessage)); err != io.EOF {
		return fmt.Errorf("%w: trailing descriptor", ErrContract)
	}
	return nil
}
