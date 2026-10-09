package sustained

import (
	"context"
	"errors"
	"fmt"
	"sort"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// colonyFoodZoneLimit is the most stockpile zones allowing human food a
// colony window may end with: the food stockpile, the raw-food
// freezer and the larder, with room for the further warehouse a full store
// asks for. A playtest ended its second day with about twenty.
const colonyFoodZoneLimit = 6

// startingSupplyDefs are the tribal start stacks the scenario forbids. Only
// Pemmican gates: wood stacks outside the base stay forbidden on purpose
// (ManageSupplySafety, outside_base:insufficient_defense), so wood is only
// reported.
var startingSupplyDefs = []string{"Pemmican", "WoodLog"}

// AuditStockpiles counts the stockpile zones the window ended with, those
// allowing human food apart, and checks the starting supplies were allowed.
// The report keeps both either way.
func AuditStockpiles(ctx context.Context, h *na.Harness, s cases.Session, report na.Report) error {
	identity := s.Identity()
	reply, err := h.Wire(ctx, "audit-zones", "observations_list_zones", map[string]any{"scope": map[string]any{"expectedIdentity": identity}, "includeFilter": true})
	if err != nil {
		return err
	}
	_, observed, err := na.Outcome(reply, "observed")
	if err != nil {
		return err
	}
	var stockpiles, food int
	var rows []map[string]any
	for _, raw := range na.AsSlice(observed["zones"]) {
		row, _ := na.AsMap(raw)
		if na.AsString(row["type"]) != "stockpile" {
			continue
		}
		stockpiles++
		isFood, _ := na.AsBool(row["foodStorage"])
		if isFood {
			food++
		}
		filter, _ := na.AsMap(row["filter"])
		rows = append(rows, map[string]any{"allowed": filter["allowedDefNames"], "id": row["id"], "label": row["label"], "priority": row["priority"], "bounds": row["bounds"], "food_storage": isFood})
	}
	defs := make([]any, len(startingSupplyDefs))
	for i, d := range startingSupplyDefs {
		defs[i] = d
	}
	supplies, err := h.Wire(ctx, "audit-supplies", "observations_list_supplies", map[string]any{
		"scope":  map[string]any{"expectedIdentity": identity},
		"filter": map[string]any{"defNames": defs},
	})
	if err != nil {
		return err
	}
	_, stock, err := na.Outcome(supplies, "observed")
	if err != nil {
		return err
	}
	var forbidden []string
	var stocks []map[string]any
	for _, raw := range na.AsSlice(stock["stocks"]) {
		row, _ := na.AsMap(raw)
		def, _ := na.AsMap(row["definition"])
		ours, unforbidden := na.AsNumber(row["ours"]), na.AsNumber(row["oursUnforbidden"])
		name := na.AsString(def["defName"])
		stocks = append(stocks, map[string]any{"def": name, "ours": ours, "ours_unforbidden": unforbidden})
		if ours > unforbidden && name == "Pemmican" {
			forbidden = append(forbidden, fmt.Sprintf("%s %v of %v still forbidden", name, ours-unforbidden, ours))
		}
	}
	churn, err := auditZoneChurn(s)
	if err != nil {
		return err
	}
	report["stockpile_zones"] = map[string]any{"stockpiles": stockpiles, "food": food, "limit": colonyFoodZoneLimit, "rows": rows, "starting_supplies": stocks, "deletes": churn.deletes, "creates": churn.creates}
	var failures []error
	if len(churn.flagged) > 0 {
		failures = append(failures, fmt.Errorf("stockpile zones deleted while their purpose still stands (churn): %v", churn.flagged))
	}
	if food > colonyFoodZoneLimit {
		failures = append(failures, fmt.Errorf("%d food stockpile zones (%d stockpiles) at the window's end, limit %d", food, stockpiles, colonyFoodZoneLimit))
	}
	if len(forbidden) > 0 {
		failures = append(failures, fmt.Errorf("starting supplies left forbidden: %v", forbidden))
	}
	return errors.Join(failures...)
}

// zoneChurn is the zone-count and churn reading of one window: one zone per
// store, and a delete only when the store's purpose is gone.
type zoneChurn struct {
	// deletes counts the admitted zone deletes by the role they name.
	deletes map[string]int
	// creates lists the admitted zone creates in journal order with their
	// tick and size: the first one is the earliest a store can open, the
	// order is the per-store creation order.
	creates []map[string]any
	// flagged lists the roles created again after a delete in the window:
	// the delete did not follow a retired purpose.
	flagged []string
}

// auditZoneChurn reads the flight recorder's admitted stockpile deletes
// and creates (layout_edit, family stockpile, attrs kind and role: a delete
// follows a purpose the department declared gone, so a later create of the
// same role is churn). It is post-hoc like the rest of the audit: a recorder that
// cannot be read leaves the counts empty.
func auditZoneChurn(s cases.Session) (zoneChurn, error) {
	churn := zoneChurn{deletes: map[string]int{}}
	flight, err := na.ReadFlight(na.FlightRecorderPath(s.Config().Output))
	if err != nil {
		return churn, nil
	}
	deleted := map[string]bool{}
	for _, row := range flight {
		if row.Kind != "layout_edit" || na.AsString(row.Payload["verdict"]) != "admitted" {
			continue
		}
		attrs, _ := na.AsMap(row.Payload["attrs"])
		role := na.AsString(attrs["role"])
		if na.AsString(attrs["family"]) != "stockpile" || role == "" {
			continue
		}
		switch na.AsString(attrs["kind"]) {
		case "delete":
			churn.deletes[role]++
			deleted[role] = true
		case "create":
			churn.creates = append(churn.creates, map[string]any{"role": role, "tick": row.Tick, "cells": attrs["cells"]})
			if deleted[role] {
				churn.flagged = append(churn.flagged, role)
			}
		}
	}
	sort.Strings(churn.flagged)
	return churn, nil
}
