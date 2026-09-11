package contractgen

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"strconv"
	"unicode/utf8"
)

// BEGIN GENERATED HELPERS

// Decoder input is bounded independently of schema field limits. Readers are not
// accepted: callers retain ownership of transport limits and cancellation.
const maxContractBytes = 1 << 20

func newContractDecoder(data []byte) (*json.Decoder, error) {
	if len(data) > maxContractBytes {
		return nil, fmt.Errorf("contract JSON exceeds %d bytes", maxContractBytes)
	}
	if err := validJSONText(data); err != nil {
		return nil, err
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	return decoder, nil
}

func endContract(decoder *json.Decoder) error {
	if _, err := decoder.Token(); err != io.EOF {
		return fmt.Errorf("trailing or malformed JSON data")
	}
	return nil
}

// encoding/json replaces invalid UTF-8 and unpaired surrogate escapes. Reject
// those before decoding so contracts cannot silently change caller-provided text.
func validJSONText(data []byte) error {
	if !utf8.Valid(data) {
		return fmt.Errorf("invalid UTF-8")
	}
	inString := false
	depth := 0
	for i := 0; i < len(data); i++ {
		if data[i] == '"' {
			inString = !inString
			continue
		}
		if !inString {
			switch data[i] {
			case '{', '[':
				depth++
				if depth > 64 {
					return fmt.Errorf("JSON exceeds 64 containers")
				}
			case '}', ']':
				depth--
			}
		}
		if !inString || data[i] != '\\' {
			continue
		}
		i++
		if i >= len(data) {
			return fmt.Errorf("incomplete string escape")
		}
		if data[i] != 'u' {
			continue
		}
		if i+4 >= len(data) {
			return fmt.Errorf("incomplete Unicode escape")
		}
		unit, err := strconv.ParseUint(string(data[i+1:i+5]), 16, 16)
		if err != nil {
			return fmt.Errorf("invalid Unicode escape")
		}
		i += 4
		if unit >= 0xDC00 && unit <= 0xDFFF {
			return fmt.Errorf("unpaired low surrogate")
		}
		if unit < 0xD800 || unit > 0xDBFF {
			continue
		}
		if i+6 >= len(data) || data[i+1] != '\\' || data[i+2] != 'u' {
			return fmt.Errorf("unpaired high surrogate")
		}
		low, err := strconv.ParseUint(string(data[i+3:i+7]), 16, 16)
		if err != nil || low < 0xDC00 || low > 0xDFFF {
			return fmt.Errorf("unpaired high surrogate")
		}
		i += 6
	}
	return nil
}

func readString(decoder *json.Decoder, maximum int, nonblank bool) (string, error) {
	value, err := decoder.Token()
	if err != nil {
		return "", err
	}
	text, ok := value.(string)
	if !ok {
		return "", fmt.Errorf("expected non-null string")
	}
	units, hasContent := 0, false
	for _, r := range text {
		units++
		if r > 0xFFFF {
			units++
		}
		if !dotNetWhitespace(r) {
			hasContent = true
		}
	}
	if units > maximum || nonblank && !hasContent {
		return "", fmt.Errorf("string violates UTF-16 length or nonblank constraint")
	}
	return text, nil
}

// Explicit Char.IsWhiteSpace set used by the net472 placement request contract.
func dotNetWhitespace(r rune) bool {
	return r >= '\t' && r <= '\r' || r == ' ' || r == 0x85 || r == 0xA0 ||
		r == 0x1680 || r >= 0x2000 && r <= 0x200A || r == 0x2028 || r == 0x2029 ||
		r == 0x202F || r == 0x205F || r == 0x3000
}

func readInteger(decoder *json.Decoder, minimum, maximum int64) (int32, error) {
	value, err := decoder.Token()
	if err != nil {
		return 0, err
	}
	number, ok := value.(json.Number)
	if !ok {
		return 0, fmt.Errorf("expected non-null integer token")
	}
	parsed, err := strconv.ParseInt(string(number), 10, 32)
	if err != nil || parsed < minimum || parsed > maximum {
		return 0, fmt.Errorf("expected integer token within [%d,%d]", minimum, maximum)
	}
	return int32(parsed), nil
}

func readBoolean(decoder *json.Decoder) (bool, error) {
	value, err := decoder.Token()
	if err != nil {
		return false, err
	}
	result, ok := value.(bool)
	if !ok {
		return false, fmt.Errorf("expected non-null boolean")
	}
	return result, nil
}

func expectDelimiter(decoder *json.Decoder, want json.Delim) error {
	value, err := decoder.Token()
	if err != nil {
		return err
	}
	if value != want {
		return fmt.Errorf("expected %c", want)
	}
	return nil
}
