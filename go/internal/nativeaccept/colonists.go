package nativeaccept

import (
	"context"
	"fmt"
)

// ListColonists reads the current map's colonists through
// observations_list_pawns at the loaded identity: includeDead adds the
// inner pawns of spawned corpses, health requests each row's health
// section. Every other detail family is off. It returns the PawnState rows;
// RowID names a row's pawn.
func ListColonists(ctx context.Context, h *Harness, label string, includeDead, health bool) ([]map[string]any, error) {
	identity, err := ReadIdentity(ctx, h, label+"-identity")
	if err != nil {
		return nil, err
	}
	filter := map[string]any{"colonist": true}
	if includeDead {
		filter["includeDead"] = true
	}
	reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
		"scope":  map[string]any{"expectedIdentity": identity},
		"filter": filter,
		"details": map[string]any{
			"needs": false, "health": health, "equipment": false, "biography": false,
			"settings": false, "social": false, "animals": false, "work": false, "schedule": false,
		},
	})
	if err != nil {
		return nil, err
	}
	_, observed, err := Outcome(reply, "observed")
	if err != nil {
		return nil, fmt.Errorf("%s: %w", label, err)
	}
	var rows []map[string]any
	for _, raw := range AsSlice(observed["pawns"]) {
		row, _ := AsMap(raw)
		rows = append(rows, row)
	}
	return rows, nil
}

// RowID is a PawnState row's pawn id.
func RowID(row map[string]any) string {
	pawn, _ := AsMap(row["pawn"])
	return AsString(pawn["id"])
}

// HediffDef is a typed Hediff's definition name.
func HediffDef(hediff map[string]any) string {
	definition, _ := AsMap(hediff["definition"])
	return AsString(definition["defName"])
}
