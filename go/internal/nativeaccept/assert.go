package nativeaccept

import "fmt"

// Small dynamic-JSON navigation helpers shared by the four acceptance binaries. The
// native reply shape is a decoded ProtoJSON document (map[string]any/[]any/etc.), so
// these mirror the loose dict access the Python scripts perform, with explicit errors
// in place of Python's bare assert. Exported for use from go/internal/nativeaccept/cmd/*.

func AsMap(v any) (map[string]any, bool) {
	m, ok := v.(map[string]any)
	return m, ok
}
func AsSlice(v any) []any {
	s, _ := v.([]any)
	return s
}
func AsString(v any) string {
	s, _ := v.(string)
	return s
}
func AsBool(v any) (bool, bool) {
	b, ok := v.(bool)
	return b, ok
}
func AsNumber(v any) float64 {
	switch n := v.(type) {
	case float64:
		return n
	case string:
		var f float64
		fmt.Sscanf(n, "%f", &f)
		return f
	default:
		return -1
	}
}

// RequireSnapshot asserts value is a populated SnapshotRef{context,entityId,token}: a
// non-empty CAS token proving the row can be used as an operation EntityPrecondition.
// It intentionally does not require an exact context match against a caller-supplied
// context, since some rows (e.g. resource items) reference a snapshot in their own
// right rather than the top-level read's context.
func RequireSnapshot(v any) error {
	snapshot, ok := AsMap(v)
	if !ok {
		return fmt.Errorf("expected a populated CAS snapshot object, found %#v", v)
	}
	if _, ok := snapshot["context"].(map[string]any); !ok {
		return fmt.Errorf("snapshot missing context: %#v", snapshot)
	}
	if id := AsString(snapshot["entityId"]); id == "" {
		return fmt.Errorf("snapshot missing entityId: %#v", snapshot)
	}
	if token := AsString(snapshot["token"]); token == "" {
		return fmt.Errorf("snapshot missing a non-empty CAS token: %#v", snapshot)
	}
	return nil
}

// RequireSocial asserts a populated PawnSocial block is present with its declared
// fields (memories/relations/situationalCacheStale may legitimately be empty/absent,
// but the block itself and the boolean staleness flag must be present).
func RequireSocial(row map[string]any) error {
	social, ok := AsMap(row["social"])
	if !ok {
		return fmt.Errorf(`expected a populated "social" block, found %#v`, row["social"])
	}
	if _, ok := social["situationalCacheStale"].(bool); !ok {
		return fmt.Errorf("social.situationalCacheStale must be an explicit boolean: %#v", social)
	}
	// memories/relations are repeated fields; ProtoJSON omits them entirely when
	// empty, so their absence is not itself a failure.
	return nil
}

func RequireIssueReason(issues []any, wantField, wantReason string) bool {
	for _, raw := range issues {
		issue, ok := AsMap(raw)
		if !ok {
			continue
		}
		if AsString(issue["field"]) != wantField {
			continue
		}
		unavailable, ok := AsMap(issue["unavailable"])
		if !ok {
			continue
		}
		if AsString(unavailable["reason"]) == wantReason {
			return true
		}
	}
	return false
}

func FailureCode(reply map[string]any) (string, bool) {
	failure, ok := AsMap(reply["failure"])
	if !ok {
		return "", false
	}
	return AsString(failure["code"]), true
}

func UnavailableReason(reply map[string]any) (string, bool) {
	unavailable, ok := AsMap(reply["unavailable"])
	if !ok {
		return "", false
	}
	return AsString(unavailable["reason"]), true
}

// CheckCompleteness asserts a Completeness message describes one complete page whose
// matched/returned counters agree with the number of rows actually returned.
func CheckCompleteness(v any, expected int) error {
	completeness, ok := AsMap(v)
	if !ok {
		return fmt.Errorf("completeness missing")
	}
	page, _ := AsMap(completeness["page"])
	if complete, _ := AsBool(page["complete"]); !complete {
		return fmt.Errorf("expected a complete page")
	}
	matched := AsNumber(completeness["matched"])
	returned := AsNumber(completeness["returned"])
	if matched != returned || int(returned) != expected {
		return fmt.Errorf("completeness counters disagree: matched=%v returned=%v expected=%d", matched, returned, expected)
	}
	return nil
}

func Contains(values []string, want string) bool {
	for _, v := range values {
		if v == want {
			return true
		}
	}
	return false
}

// Merge returns a shallow union of base and extra, with extra's keys taking priority.
func Merge(base map[string]any, extra map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range base {
		out[k] = v
	}
	for k, v := range extra {
		out[k] = v
	}
	return out
}

// DeepEqual compares two decoded-JSON values for structural equality. fmt formats
// maps with their keys sorted, so this is stable across the random Go map iteration
// order for the JSON shapes these binaries compare (no floating-point rounding
// concerns beyond what ProtoJSON itself already introduces on the wire).
func DeepEqual(a, b any) bool {
	return fmt.Sprint(a) == fmt.Sprint(b)
}
