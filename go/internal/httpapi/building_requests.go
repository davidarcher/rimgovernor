package httpapi

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"strconv"
	"unicode/utf8"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/store"
)

const buildingRequestLimit = 8192

// These decoders establish syntax and typed intent only. Native facts, admission
// and explicit authority remain responsibilities of their eventual callers.
func decodeBuildingSubmission(reader io.Reader) (store.SubmissionRequest, error) {
	var result store.SubmissionRequest
	fields, err := buildingRequest(reader, "requestId", "expected", "building")
	if err != nil {
		return result, err
	}
	if err = json.Unmarshal(fields["requestId"], &result.RequestID); err != nil {
		return result, err
	}
	if err = buildingRequestID(result.RequestID); err != nil {
		return result, err
	}
	if result.World, err = buildingWorld(fields["expected"]); err != nil {
		return result, err
	}
	b, err := buildingFields(fields["building"], "defName", "x", "z", "rotation", "stuff")
	if err != nil {
		return result, err
	}
	var definition, stuff string
	var rotation domain.Rotation
	var cell domain.Cell
	for key, target := range map[string]any{"defName": &definition, "stuff": &stuff, "rotation": &rotation, "x": &cell.X, "z": &cell.Z} {
		if err = json.Unmarshal(b[key], target); err != nil {
			return result, err
		}
	}
	result.Building, err = domain.NewBuilding(definition, cell, rotation, stuff)
	return result, err
}
func decodeBuildingAcquire(reader io.Reader) (store.ControlRequest, error) {
	result := store.ControlRequest{Kind: store.AcquireControl}
	fields, err := buildingRequest(reader, "requestId", "expected", "planId", "revision")
	if err != nil {
		return result, err
	}
	if err = json.Unmarshal(fields["requestId"], &result.RequestID); err != nil {
		return result, err
	}
	if err = buildingRequestID(result.RequestID); err != nil {
		return result, err
	}
	if result.World, err = buildingWorld(fields["expected"]); err != nil {
		return result, err
	}
	if err = json.Unmarshal(fields["planId"], &result.Plan); err != nil {
		return result, err
	}
	if err = buildingRequestID(string(result.Plan)); err != nil {
		return result, err
	}
	revision, err := buildingUint(fields["revision"])
	if err != nil || revision == 0 {
		return result, errors.New("revision must be a positive canonical uint64 string")
	}
	result.Revision = domain.PlanRevision(revision)
	return result, nil
}
func decodeBuildingManual(reader io.Reader) (store.ControlRequest, error) {
	result := store.ControlRequest{Kind: store.ManualControl}
	fields, err := buildingRequest(reader, "requestId", "expected")
	if err != nil {
		return result, err
	}
	if err = json.Unmarshal(fields["requestId"], &result.RequestID); err != nil {
		return result, err
	}
	if err = buildingRequestID(result.RequestID); err != nil {
		return result, err
	}
	result.World, err = buildingWorld(fields["expected"])
	return result, err
}
func buildingRequestID(id string) error {
	_, err := domain.NewPlan(domain.PlanID(id), 1, nil)
	return err
}
func buildingUint(raw json.RawMessage) (uint64, error) {
	var value string
	if err := json.Unmarshal(raw, &value); err != nil {
		return 0, err
	}
	n, err := strconv.ParseUint(value, 10, 64)
	if err != nil || strconv.FormatUint(n, 10) != value {
		return 0, errors.New("expected canonical uint64 string")
	}
	return n, nil
}
func buildingWorld(raw json.RawMessage) (store.World, error) {
	var world store.World
	f, err := buildingFields(raw, "colonyId", "loadToken", "mapId")
	if err != nil {
		return world, err
	}
	for key, target := range map[string]any{"colonyId": &world.Colony, "loadToken": &world.Load, "mapId": &world.Map} {
		if err = json.Unmarshal(f[key], target); err != nil {
			return world, err
		}
	}
	return world, world.Validate()
}
func buildingFields(raw []byte, keys ...string) (map[string]json.RawMessage, error) {
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(raw, &fields); err != nil {
		return nil, err
	}
	if len(fields) != len(keys) {
		return nil, errors.New("unexpected or missing fields")
	}
	for _, key := range keys {
		value, ok := fields[key]
		if !ok || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return nil, errors.New("required field missing or null")
		}
	}
	return fields, nil
}

// buildingOptionalFields is buildingFields for an object that carries a fixed
// set of required keys plus a known set of optional ones. Required keys must be
// present and non-null exactly as buildingFields demands; an optional key may be
// absent, but a present one must be non-null, and no other key is tolerated. An
// absent optional key is absent from the result, so callers distinguish "not
// supplied" from "supplied empty" with the two-value map read.
func buildingOptionalFields(raw []byte, required, optional []string) (map[string]json.RawMessage, error) {
	var fields map[string]json.RawMessage
	if err := json.Unmarshal(raw, &fields); err != nil {
		return nil, err
	}
	known := make(map[string]bool, len(required)+len(optional))
	for _, key := range required {
		value, ok := fields[key]
		if !ok || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return nil, errors.New("required field missing or null")
		}
		known[key] = true
	}
	for _, key := range optional {
		if value, ok := fields[key]; ok && bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return nil, errors.New("optional field present but null")
		}
		known[key] = true
	}
	for key := range fields {
		if !known[key] {
			return nil, errors.New("unexpected field")
		}
	}
	return fields, nil
}
func buildingRequest(reader io.Reader, keys ...string) (map[string]json.RawMessage, error) {
	data, err := io.ReadAll(io.LimitReader(reader, buildingRequestLimit+1))
	if err != nil {
		return nil, err
	}
	if len(data) > buildingRequestLimit || !utf8.Valid(data) || !buildingScalarStrings(data) {
		return nil, errors.New("request exceeds byte limit or contains invalid Unicode")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if err = buildingUniqueJSON(decoder, 0); err != nil {
		return nil, err
	}
	if _, err = decoder.Token(); err != io.EOF {
		return nil, errors.New("trailing JSON")
	}
	return buildingFields(data, keys...)
}
func buildingUniqueJSON(d *json.Decoder, depth int) error {
	if depth > 8 {
		return errors.New("request nesting exceeds limit")
	}
	token, err := d.Token()
	if err != nil {
		return err
	}
	delim, ok := token.(json.Delim)
	if !ok {
		return nil
	}
	if delim != '{' && delim != '[' {
		return errors.New("invalid JSON delimiter")
	}
	seen := map[string]bool{}
	for d.More() {
		if delim == '{' {
			key, err := d.Token()
			if err != nil {
				return err
			}
			text, ok := key.(string)
			if !ok || seen[text] {
				return errors.New("duplicate JSON key")
			}
			seen[text] = true
		}
		if err = buildingUniqueJSON(d, depth+1); err != nil {
			return err
		}
	}
	_, err = d.Token()
	return err
}

// encoding/json replaces lone surrogate escapes. Reject them before decoding so
// identifiers never change silently; ordinary escaped backslashes stay literal.
func buildingScalarStrings(data []byte) bool {
	for i := 0; i < len(data); i++ {
		if data[i] != '\\' {
			continue
		}
		i++
		if i >= len(data) {
			return false
		}
		if data[i] != 'u' {
			continue
		}
		if i+4 >= len(data) {
			return false
		}
		n, e := strconv.ParseUint(string(data[i+1:i+5]), 16, 16)
		if e != nil {
			return false
		}
		i += 4
		if n >= 0xDC00 && n <= 0xDFFF {
			return false
		}
		if n < 0xD800 || n > 0xDBFF {
			continue
		}
		if i+6 >= len(data) || data[i+1] != '\\' || data[i+2] != 'u' {
			return false
		}
		low, e := strconv.ParseUint(string(data[i+3:i+7]), 16, 16)
		if e != nil || low < 0xDC00 || low > 0xDFFF {
			return false
		}
		i += 6
	}
	return true
}
