package nativeaccept

import (
	"context"
	"fmt"
)

// BuildingRows is the player building table the bundle carries (every
// status), keyed by building id: the canonical rows a section's building
// references resolve against (#1343).
func (h *Harness) BuildingRows(ctx context.Context, label string, identity any) (map[string]map[string]any, error) {
	reply, err := h.Wire(ctx, label, "observations_list_buildings", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "statuses": []any{"all"}, "playerOnly": true,
	})
	if err != nil {
		return nil, err
	}
	_, listed, err := Outcome(reply, "observed")
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	out := map[string]map[string]any{}
	for _, raw := range AsSlice(listed["buildings"]) {
		row, _ := AsMap(raw)
		ref, _ := AsMap(row["building"])
		out[AsString(ref["id"])] = row
	}
	return out, nil
}
