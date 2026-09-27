package nativeaccept

import (
	"context"
	"fmt"
	"strings"
)

// ApplyOne sends one Actions/Apply intent under key and returns its result:
// a map holding "applied" (a receipt) or "refused" (code, reason).
func ApplyOne(ctx context.Context, h *Harness, label string, identity map[string]any, key string, intent map[string]any) (map[string]any, error) {
	action := map[string]any{"key": key}
	for k, v := range intent {
		action[k] = v
	}
	reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{action}})
	if err != nil {
		return nil, err
	}
	results := AsSlice(reply["results"])
	if len(results) != 1 {
		return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
	}
	result, _ := AsMap(results[0])
	return result, nil
}

// AppliedJob is the job evidence of an applied result, or an error naming
// the refusal.
func AppliedJob(label string, result map[string]any) (map[string]any, error) {
	receipt, _ := AsMap(result["applied"])
	applied, ok := AsMap(receipt["applied"])
	if !ok {
		return nil, fmt.Errorf("%s: expected applied, got %#v", label, result)
	}
	observed, _ := AsMap(applied["observed"])
	job, _ := AsMap(observed["job"])
	return job, nil
}

// Refused checks a result is a refusal whose reason contains want.
func Refused(label string, result map[string]any, want string) error {
	refusal, ok := AsMap(result["refused"])
	if !ok || !strings.Contains(AsString(refusal["reason"]), want) {
		return fmt.Errorf("%s: expected a refusal containing %q, got %#v", label, want, result)
	}
	return nil
}
