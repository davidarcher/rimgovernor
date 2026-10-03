package sustained

import (
	"context"
	"errors"
	"fmt"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// colonyFoodZoneLimit is the most stockpile zones allowing human food a
// colony window may end with (#1581): the food stockpile, the raw-food
// freezer and the larder, with headroom for one growth fragment. A playtest
// ended its second day with about twenty.
const colonyFoodZoneLimit = 6

// startingSupplyDefs are the tribal start's stacks the scenario forbids; a
// window of several days must leave every one of ours unforbidden (#1581).
var startingSupplyDefs = []string{"Pemmican", "WoodLog"}

// AuditStockpiles counts the stockpile zones the window ended with, those
// allowing human food apart, and checks the starting supplies were allowed.
// The report keeps both either way.
func AuditStockpiles(ctx context.Context, h *na.Harness, s cases.Session, report na.Report) error {
	identity := s.Identity()
	reply, err := h.Wire(ctx, "audit-zones", "observations_list_zones", map[string]any{"scope": map[string]any{"expectedIdentity": identity}})
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
		rows = append(rows, map[string]any{"id": row["id"], "label": row["label"], "priority": row["priority"], "bounds": row["bounds"], "food_storage": isFood})
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
		if ours > unforbidden {
			forbidden = append(forbidden, fmt.Sprintf("%s %v of %v still forbidden", name, ours-unforbidden, ours))
		}
	}
	report["stockpile_zones"] = map[string]any{"stockpiles": stockpiles, "food": food, "limit": colonyFoodZoneLimit, "rows": rows, "starting_supplies": stocks}
	var failures []error
	if food > colonyFoodZoneLimit {
		failures = append(failures, fmt.Errorf("%d food stockpile zones (%d stockpiles) at the window's end, limit %d", food, stockpiles, colonyFoodZoneLimit))
	}
	if len(forbidden) > 0 {
		failures = append(failures, fmt.Errorf("starting supplies left forbidden: %v", forbidden))
	}
	return errors.Join(failures...)
}
