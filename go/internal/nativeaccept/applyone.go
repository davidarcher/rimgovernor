package nativeaccept

import (
	"context"
	"fmt"
	"strings"
)

// GiveJob is the GiveJobIntent of pawn taking the vanilla JobDef
// job on targets, in job target order.
func GiveJob(pawn, job string, targets ...string) map[string]any {
	refs := make([]any, 0, len(targets))
	for _, t := range targets {
		refs = append(refs, map[string]any{"id": t})
	}
	return map[string]any{"giveJob": map[string]any{"pawn": map[string]any{"id": pawn}, "job": job, "targets": refs}}
}

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

// ApplyDraft drafts or undrafts pawn through a DraftIntent and
// returns the applied job evidence (issued is false when the pawn already
// stood that way), or an error naming a refusal.
func ApplyDraft(ctx context.Context, h *Harness, label string, identity map[string]any, key, pawn string, drafted bool) (map[string]any, error) {
	result, err := ApplyOne(ctx, h, label, identity, key, map[string]any{"draft": map[string]any{"pawnId": pawn, "drafted": drafted}})
	if err != nil {
		return nil, err
	}
	return AppliedJob(label, result)
}

// ApplyCombatOrders sends one combat_orders batch as an Actions/Apply
// intent under key and returns its per-order results. The intent always
// applies; each order carries its own applied or refusal.
func ApplyCombatOrders(ctx context.Context, h *Harness, label string, identity map[string]any, key string, orders []any) ([]map[string]any, error) {
	result, err := ApplyOne(ctx, h, label, identity, key, map[string]any{"combatOrders": map[string]any{"orders": orders}})
	if err != nil {
		return nil, err
	}
	receipt, _ := AsMap(result["applied"])
	applied, ok := AsMap(receipt["applied"])
	if !ok {
		return nil, fmt.Errorf("%s: combat orders not applied: %#v", label, result)
	}
	observed, _ := AsMap(applied["observed"])
	effect, _ := AsMap(observed["combatOrders"])
	rows := AsSlice(effect["results"])
	if len(rows) != len(orders) {
		return nil, fmt.Errorf("%s: %d results for %d orders: %#v", label, len(rows), len(orders), result)
	}
	out := make([]map[string]any, len(rows))
	for i, r := range rows {
		out[i], _ = AsMap(r)
	}
	return out, nil
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
