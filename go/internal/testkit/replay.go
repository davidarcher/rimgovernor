package testkit

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"unicode/utf8"
)

// Replay bounds apply to each input independently, including its whitespace.
const (
	MaxReplayBytes = 8 << 20
	MaxReplayDepth = 128
)

// Comparison describes the first difference in whitespace-compacted evidence.
// FirstDifference is a zero-based byte offset, or -1 when Equal is true.
type Comparison struct {
	Equal           bool `json:"equal"`
	FirstDifference int  `json:"first_difference"`
}

// CompareJSON compares offline evidence, not controller decisions or native truth.
// Only insignificant JSON whitespace is removed. Object and array order, string
// escape spelling, number lexemes, unknown fields and nulls remain significant.
// Readers must terminate; size/depth limits do not impose an I/O deadline.
func CompareJSON(expected, candidate io.Reader) (Comparison, error) {
	left, err := replayJSON(expected)
	if err != nil {
		return Comparison{}, fmt.Errorf("expected evidence: %w", err)
	}
	right, err := replayJSON(candidate)
	if err != nil {
		return Comparison{}, fmt.Errorf("candidate evidence: %w", err)
	}
	for i := 0; i < len(left) && i < len(right); i++ {
		if left[i] != right[i] {
			return Comparison{FirstDifference: i}, nil
		}
	}
	if len(left) != len(right) {
		return Comparison{FirstDifference: min(len(left), len(right))}, nil
	}
	return Comparison{Equal: true, FirstDifference: -1}, nil
}

func replayJSON(reader io.Reader) ([]byte, error) {
	if reader == nil {
		return nil, fmt.Errorf("missing reader")
	}
	data, err := io.ReadAll(io.LimitReader(reader, MaxReplayBytes+1))
	if err != nil {
		return nil, fmt.Errorf("read: %w", err)
	}
	if len(data) > MaxReplayBytes {
		return nil, fmt.Errorf("JSON exceeds %d bytes", MaxReplayBytes)
	}
	if !utf8.Valid(data) {
		return nil, fmt.Errorf("JSON contains invalid UTF-8")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	if err := replayValue(decoder, 0); err != nil {
		return nil, fmt.Errorf("invalid JSON: %w", err)
	}
	if _, err := decoder.Token(); err != io.EOF {
		if err != nil {
			return nil, fmt.Errorf("trailing JSON: %w", err)
		}
		return nil, fmt.Errorf("trailing JSON value")
	}
	var compact bytes.Buffer
	if err := json.Compact(&compact, data); err != nil {
		return nil, fmt.Errorf("invalid JSON: %w", err)
	}
	return compact.Bytes(), nil
}

// Token decoding is confined to validation of raw replay evidence. We retain the
// original bytes for comparison rather than reserializing generic decoded values.
func replayValue(decoder *json.Decoder, depth int) error {
	token, err := decoder.Token()
	if err != nil {
		return err
	}
	delimiter, container := token.(json.Delim)
	if !container {
		return nil
	}
	if delimiter != '{' && delimiter != '[' {
		return fmt.Errorf("unexpected closing delimiter")
	}
	if depth >= MaxReplayDepth {
		return fmt.Errorf("nesting exceeds %d containers", MaxReplayDepth)
	}
	if delimiter == '{' {
		keys := make(map[string]struct{})
		for decoder.More() {
			keyToken, err := decoder.Token()
			if err != nil {
				return err
			}
			key, ok := keyToken.(string)
			if !ok {
				return fmt.Errorf("object key must be a string")
			}
			if _, duplicate := keys[key]; duplicate {
				return fmt.Errorf("duplicate object field at byte %d", decoder.InputOffset())
			}
			keys[key] = struct{}{}
			if err := replayValue(decoder, depth+1); err != nil {
				return err
			}
		}
	} else {
		for decoder.More() {
			if err := replayValue(decoder, depth+1); err != nil {
				return err
			}
		}
	}
	_, err = decoder.Token() // The decoder enforces the matching closing delimiter.
	return err
}
