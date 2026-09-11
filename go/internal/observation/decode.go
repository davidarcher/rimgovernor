package observation

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"strings"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation/wire"
)

// object rejects duplicate selected or unselected keys and trailing data before
// projection. RawMessage keeps numeric tokens and strings intact for generation.
func object(raw json.RawMessage) (map[string]json.RawMessage, error) {
	if len(raw) == 0 || len(raw) > 4<<20 {
		return nil, fmt.Errorf("%w: object size", ErrContract)
	}
	d := json.NewDecoder(bytes.NewReader(raw))
	token, err := d.Token()
	if err != nil || token != json.Delim('{') {
		return nil, fmt.Errorf("%w: expected object", ErrContract)
	}
	fields := map[string]json.RawMessage{}
	for d.More() {
		token, err = d.Token()
		if err != nil {
			return nil, fmt.Errorf("%w: object key", ErrContract)
		}
		key, ok := token.(string)
		if !ok {
			return nil, fmt.Errorf("%w: object key", ErrContract)
		}
		if _, exists := fields[key]; exists {
			return nil, fmt.Errorf("%w: duplicate %s", ErrContract, key)
		}
		var value json.RawMessage
		if err = d.Decode(&value); err != nil {
			return nil, fmt.Errorf("%w: object value", ErrContract)
		}
		fields[key] = value
	}
	if token, err = d.Token(); err != nil || token != json.Delim('}') {
		return nil, fmt.Errorf("%w: object end", ErrContract)
	}
	if _, err = d.Token(); err != io.EOF {
		return nil, fmt.Errorf("%w: trailing data", ErrContract)
	}
	return fields, nil
}
func projection(fields map[string]json.RawMessage, keys ...string) ([]byte, error) {
	selected := map[string]json.RawMessage{}
	for _, key := range keys {
		if value, ok := fields[key]; ok {
			selected[key] = value
		}
	}
	return json.Marshal(selected)
}

// DecodeIdentity selects the native identity fields; receipt metadata and other
// additive native fields remain available in Reading.Receipts, outside this schema.
func DecodeIdentity(raw json.RawMessage) (Identity, error) {
	fields, err := object(raw)
	if err != nil {
		return Identity{}, err
	}
	selected, err := projection(fields, "success", "colonyId", "loadToken", "mapId", "tick", "observationBatchVersion", "placementPreviewBatchVersion")
	if err != nil {
		return Identity{}, fmt.Errorf("%w: identity projection", ErrContract)
	}
	value, err := wire.DecodeColonyIdentity(selected)
	if err != nil {
		return Identity{}, fmt.Errorf("%w: identity: %w", ErrContract, err)
	}
	if !value.Success {
		return Identity{}, fmt.Errorf("%w: identity refused", ErrContract)
	}
	identity := Identity{Colony: domain.ColonyID(value.ColonyId), Map: domain.MapID(value.MapId), Load: domain.LoadID(value.LoadToken), Tick: domain.Tick(value.Tick), ObservationBatchVersion: value.ObservationBatchVersion, PlacementPreviewBatchVersion: value.PlacementPreviewBatchVersion}
	return identity, identity.Validate()
}

func DecodeStatus(raw json.RawMessage) (Status, error) {
	fields, err := object(raw)
	if err != nil {
		return Status{}, err
	}
	if string(fields["success"]) != "true" {
		return Status{}, fmt.Errorf("%w: status success missing or false", ErrContract)
	}
	selected := map[string]json.RawMessage{"status": fields["status"]}
	clockAvailable, err := clockReadable(fields["skipped"])
	if err != nil {
		return Status{}, err
	}
	var availability string
	if json.Unmarshal(fields["status"], &availability) != nil {
		return Status{}, fmt.Errorf("%w: status unavailable", ErrContract)
	}
	switch Availability(availability) {
	case GameLoaded:
	case NoMap, NoGame:
		clockAvailable = false
	default:
		return Status{}, fmt.Errorf("%w: unsupported status", ErrContract)
	}
	if clockAvailable && len(fields["time"]) != 0 && string(fields["time"]) != "null" {
		clock, err := object(fields["time"])
		if err != nil {
			return Status{}, err
		}
		for _, key := range []string{"paused", "forcePaused"} {
			if raw := clock[key]; len(raw) != 0 && string(raw) != "null" {
				var value bool
				if json.Unmarshal(raw, &value) != nil {
					return Status{}, fmt.Errorf("%w: clock boolean", ErrContract)
				}
				// StatusTool.TimeBlock uses false for getter failures. Only true is known.
				if value {
					selected[key] = json.RawMessage("true")
				}
			}
		}
		if raw := clock["timeSpeed"]; len(raw) != 0 && string(raw) != "null" {
			var speed string
			if json.Unmarshal(raw, &speed) != nil {
				return Status{}, fmt.Errorf("%w: time speed", ErrContract)
			}
			switch Speed(speed) {
			case Paused, Normal, Fast, Superfast, Ultrafast:
			default:
				return Status{}, fmt.Errorf("%w: unsupported speed", ErrContract)
			}
			selected["timeSpeed"] = raw
			if Speed(speed) == Paused {
				selected["paused"] = json.RawMessage("true")
			}
		}
		if string(selected["forcePaused"]) == "true" {
			selected["paused"] = json.RawMessage("true")
		}
	}
	encoded, err := json.Marshal(selected)
	if err != nil {
		return Status{}, fmt.Errorf("%w: status projection", ErrContract)
	}
	value, err := wire.DecodeStatusClock(encoded)
	if err != nil {
		return Status{}, fmt.Errorf("%w: status projection: %w", ErrContract, err)
	}
	result := Status{Availability: Availability(value.Status)}
	if value.Paused != nil {
		result.Paused = domain.Known(*value.Paused)
	}
	if value.ForcePaused != nil {
		result.ForcePaused = domain.Known(*value.ForcePaused)
	}
	if value.TimeSpeed != nil {
		result.Speed = domain.Known(Speed(*value.TimeSpeed))
	}
	return result, nil
}

func clockReadable(raw json.RawMessage) (bool, error) {
	if len(raw) == 0 || string(raw) == "null" {
		return false, nil
	}
	var rows []json.RawMessage
	if json.Unmarshal(raw, &rows) != nil {
		return false, fmt.Errorf("%w: skipped entries", ErrContract)
	}
	readable := true
	for _, row := range rows {
		fields, err := object(row)
		if err != nil {
			return false, err
		}
		var field string
		if json.Unmarshal(fields["field"], &field) != nil || field == "" {
			return false, fmt.Errorf("%w: skipped field missing", ErrContract)
		}
		if field == "time" || field == "time.paused" || field == "time.forcePaused" || field == "time.timeSpeed" || strings.HasPrefix(field, "time.clock") {
			readable = false
		}
	}
	return readable, nil
}
