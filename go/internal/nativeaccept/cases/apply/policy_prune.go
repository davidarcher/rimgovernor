package apply

import (
	"context"
	"fmt"
	"slices"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "apply/policy-prune",
		Scope: "PolicyPruneIntent on Actions/Apply (#1298): an extra outfit and an extra allowed area, a colonist assigned " +
			"to both, are deleted; the colonist first moves onto its own outfit labelled with its short name and becomes " +
			"unrestricted; a resent prune applies again with nothing deleted. A Go test cannot see vanilla TryDelete or the " +
			"reassignment.",
		Start:  cases.LabStart(),
		Budget: 2 * time.Minute,
		Run:    policyPrune,
	})
}

func policyPrune(ctx context.Context, s cases.Session) error {
	h, identity, report := s.Harness(), s.Identity(), s.Report()
	fixture := func(mode string) (map[string]any, error) {
		return h.Call(ctx, mode, "test/gear_fixture", map[string]any{"mode": mode})
	}
	before, err := fixture("prune_setup")
	if err != nil {
		return err
	}
	report["setup"] = before
	pawn, outfit, area := na.AsString(before["pawn"]), na.AsString(before["outfit"]), na.AsString(before["area"])
	if pawn == "" || outfit == "" || area == "" {
		return fmt.Errorf("prune fixture assigned nothing: %#v", before)
	}
	if _, err := na.GrantAuto(ctx, h.WireFunc(), "acquire", identity); err != nil {
		return err
	}
	list := func(v any) []string {
		var r []string
		for _, x := range na.AsSlice(v) {
			r = append(r, na.AsString(x))
		}
		return r
	}
	prune := func(key, db, id string, wantDeleted bool) error {
		reply, err := h.Wire(ctx, key, "operations_apply", map[string]any{"identity": identity, "actions": []any{map[string]any{"key": key, "policyPrune": map[string]any{"database": db, "deleteIds": []any{id}}}}})
		if err != nil {
			return err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return fmt.Errorf("%s: expected one result, got %#v", key, reply)
		}
		result, _ := na.AsMap(results[0])
		applied, _ := na.AsMap(result["applied"])
		applied, _ = na.AsMap(applied["applied"])
		observed, _ := na.AsMap(applied["observed"])
		effect, ok := na.AsMap(observed["policyPrune"])
		if !ok {
			return fmt.Errorf("%s: expected an applied policy prune, got %#v", key, result)
		}
		deleted, moved := list(effect["deletedIds"]), list(effect["reassignedPawnIds"])
		if wantDeleted && (!slices.Equal(deleted, []string{id}) || !slices.Contains(moved, pawn)) || !wantDeleted && (len(deleted) != 0 || len(moved) != 0) {
			return fmt.Errorf("%s: expected deleted=%v with %s moved, got %#v", key, wantDeleted, pawn, effect)
		}
		report[key] = effect
		return nil
	}
	if err := prune("prune-outfit", "POLICY_DATABASE_OUTFIT", outfit, true); err != nil {
		return err
	}
	if err := prune("prune-area", "POLICY_DATABASE_ALLOWED_AREA", area, true); err != nil {
		return err
	}
	after, err := fixture("prune_read")
	if err != nil {
		return err
	}
	report["after"] = after
	if slices.Contains(list(after["outfits"]), outfit) || slices.Contains(list(after["areas"]), area) {
		return fmt.Errorf("pruned policy or area still stands: %#v", after)
	}
	if na.AsString(after["outfitLabel"]) != na.AsString(before["shortName"]) || na.AsString(after["outfit"]) == outfit || na.AsString(after["area"]) != "" {
		return fmt.Errorf("pawn not moved onto its own outfit and off the area: %#v", after)
	}
	if err := prune("prune-outfit-again", "POLICY_DATABASE_OUTFIT", outfit, false); err != nil {
		return err
	}
	return prune("prune-area-again", "POLICY_DATABASE_ALLOWED_AREA", area, false)
}
