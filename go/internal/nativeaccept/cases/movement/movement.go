// Package movement holds the movement case: a move intent through
// Actions/Apply orders a real native Goto for an owned drafted pawn, and the
// same order resent under a new key applies again as a no-op.
package movement

import (
	"context"
	"fmt"
	"sort"
	"time"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

func init() {
	cases.Register(cases.Case{
		Name: "movement/arrival",
		Scope: "A move intent (Actions/Apply) orders a real native Goto for an owned drafted pawn; the same order " +
			"resent under a new key applies as a no-op. A Go test cannot see native reachability or the job it issues.",
		Start:  cases.LabStart(),
		Budget: 5 * time.Minute,
		Run:    run,
	})
}

func run(ctx context.Context, s cases.Session) error {
	report := s.Report()
	h, identity := s.Harness(), s.Identity()
	read := func(label, pawnID string) (map[string]any, error) {
		filter := map[string]any{"colonist": true, "downed": false}
		if pawnID != "" {
			filter["ids"] = []any{pawnID}
		}
		reply, err := h.Wire(ctx, label, "observations_list_pawns", map[string]any{
			"scope": map[string]any{"expectedIdentity": identity}, "filter": filter,
		})
		if err != nil {
			return nil, err
		}
		return na.PawnRow(reply, identity, pawnID)
	}
	apply := func(label, key, pawnID string, cell map[string]any) (map[string]any, error) {
		reply, err := h.Wire(ctx, label, "operations_apply", map[string]any{"identity": identity, "actions": []any{
			map[string]any{"key": key, "move": map[string]any{"pawnId": pawnID, "destination": cell}},
		}})
		if err != nil {
			return nil, err
		}
		results := na.AsSlice(reply["results"])
		if len(results) != 1 {
			return nil, fmt.Errorf("%s: expected one result, got %#v", label, reply)
		}
		result, _ := na.AsMap(results[0])
		return result, nil
	}

	before, err := read("initial-pawn", "")
	if err != nil {
		return err
	}
	pawn, _ := na.AsMap(before["pawn"])
	pawnID := na.AsString(pawn["id"])
	undrafted, err := apply("undrafted", "move-undrafted", pawnID, map[string]any{"x": 0, "z": 0})
	if err != nil {
		return err
	}
	if _, refused := undrafted["refused"]; !refused {
		return fmt.Errorf("undrafted: a move for an undrafted pawn was not refused: %#v", undrafted)
	}
	grant, err := na.GrantAuto(ctx, h.WireFunc(), "grant", identity)
	if err != nil {
		return err
	}
	draftReply, err := h.Wire(ctx, "draft", "operations_execute", na.ExecuteRequest(identity, grant, before, 1))
	if err != nil {
		return err
	}
	if _, _, err = na.Outcome(draftReply, "receipt"); err != nil {
		return err
	}
	owned, err := read("owned", pawnID)
	if err != nil {
		return err
	}
	ownedPawn, _ := na.AsMap(owned["pawn"])
	origin, _ := na.AsMap(ownedPawn["position"])
	originX, originZ := na.AsNumber(origin["x"]), na.AsNumber(origin["z"])
	var exactCells []any
	for dx := -5; dx <= 5; dx++ {
		for dz := -5; dz <= 5; dz++ {
			distance := dx*dx + dz*dz
			x, z := originX+float64(dx), originZ+float64(dz)
			if distance < 4 || distance > 25 || x < 0 || z < 0 {
				continue
			}
			exactCells = append(exactCells, map[string]any{"x": x, "z": z})
		}
	}
	cellsReply, err := h.Wire(ctx, "candidate-cells", "observations_get_cells", map[string]any{
		"scope": map[string]any{"expectedIdentity": identity}, "exactCells": map[string]any{"cells": exactCells},
		"fields": map[string]any{"terrain": true, "visibility": true, "traversal": true},
	})
	if err != nil {
		return err
	}
	_, observedCells, err := na.Outcome(cellsReply, "observed")
	if err != nil {
		return err
	}
	candidateCells, err := candidates(observedCells, origin)
	if err != nil {
		return err
	}
	// Native judges reachability when the move applies: the first
	// candidate it applies is the destination.
	var destination map[string]any
	for number, cell := range candidateCells[:min(len(candidateCells), 24)] {
		result, err := apply(fmt.Sprintf("move-%d", number), fmt.Sprintf("move-%d", number), pawnID, cell)
		if err != nil {
			return err
		}
		if _, ok := result["applied"]; ok {
			destination = cell
			break
		}
		if _, ok := result["refused"]; !ok {
			return fmt.Errorf("move-%d: neither applied nor refused: %#v", number, result)
		}
	}
	if destination == nil {
		return fmt.Errorf("no nearby destination applied")
	}
	moving, err := read("moving", pawnID)
	if err != nil {
		return err
	}
	if def := na.AsString(dig(moving, "job", "defName")); def != "Goto" {
		return fmt.Errorf("moving: pawn job is %q, not Goto", def)
	}
	again, err := apply("resend", "move-resend", pawnID, destination)
	if err != nil {
		return err
	}
	if _, ok := again["applied"]; !ok {
		return fmt.Errorf("resend: a matching order was not applied as a no-op: %#v", again)
	}
	if err := cases.CheckStartupLog(s); err != nil {
		return err
	}
	report["pawn_id"] = pawnID
	report["destination"] = destination
	return nil
}

// candidates filters and orders an observations_get_cells reply's cells to the exact
// set of nearby, walkable, passable, unfogged candidates.
func candidates(snapshot, origin map[string]any) ([]map[string]any, error) {
	rows := na.AsSlice(snapshot["cells"])
	originX, originZ := na.AsNumber(origin["x"]), na.AsNumber(origin["z"])
	var result []map[string]any
	for _, raw := range rows {
		row, _ := na.AsMap(raw)
		cell, _ := na.AsMap(row["cell"])
		cx, cz := na.AsNumber(cell["x"]), na.AsNumber(cell["z"])
		distance := (cx-originX)*(cx-originX) + (cz-originZ)*(cz-originZ)
		walkable, _ := na.AsBool(row["walkable"])
		passable, _ := na.AsBool(row["passable"])
		fogged, _ := na.AsBool(row["fogged"])
		if distance >= 4 && distance <= 25 && walkable && passable && !fogged {
			if row["terrain"] == nil {
				return nil, fmt.Errorf("candidate cells: missing terrain for a walkable cell: %#v", row)
			}
			result = append(result, cell)
		}
	}
	sort.Slice(result, func(i, j int) bool {
		xi, zi := na.AsNumber(result[i]["x"]), na.AsNumber(result[i]["z"])
		xj, zj := na.AsNumber(result[j]["x"]), na.AsNumber(result[j]["z"])
		di := (xi-originX)*(xi-originX) + (zi-originZ)*(zi-originZ)
		dj := (xj-originX)*(xj-originX) + (zj-originZ)*(zj-originZ)
		if di != dj {
			return di < dj
		}
		if xi != xj {
			return xi < xj
		}
		return zi < zj
	})
	return result, nil
}

func dig(m map[string]any, path ...string) any {
	var current any = m
	for _, key := range path {
		next, ok := na.AsMap(current)
		if !ok {
			return nil
		}
		current = next[key]
	}
	return current
}
