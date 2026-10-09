package nativeaccept

import (
	"context"
	"fmt"
)

// PawnRows is the map's pawn table as the list read answers it (every
// spawned pawn, every detail family), keyed by pawn id: the canonical rows
// a section's pawn references resolve against.
func (h *Harness) PawnRows(ctx context.Context, label string, identity any) (map[string]map[string]any, error) {
	reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
	if err != nil {
		return nil, err
	}
	_, listed, err := Outcome(reply, "observed")
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	out := map[string]map[string]any{}
	for _, raw := range AsSlice(listed["pawns"]) {
		row, _ := AsMap(raw)
		ref, _ := AsMap(row["pawn"])
		out[AsString(ref["id"])] = row
	}
	return out, nil
}

// PawnRef is the id a section row's pawn reference names.
func PawnRef(row map[string]any) string {
	ref, _ := AsMap(row["pawn"])
	return AsString(ref["id"])
}

// JoinPawn replaces row's pawn reference with its table row, so a case
// reads the pawn's facts where the section once embedded them.
func (h *Harness) JoinPawn(ctx context.Context, label string, identity any, row map[string]any) error {
	pawns, err := h.PawnRows(ctx, label+"-pawns", identity)
	if err != nil {
		return err
	}
	id := PawnRef(row)
	pawn, ok := pawns[id]
	if !ok {
		return fmt.Errorf("%s: pawn %s missing from the pawn table", label, id)
	}
	row["pawn"] = pawn
	return nil
}
