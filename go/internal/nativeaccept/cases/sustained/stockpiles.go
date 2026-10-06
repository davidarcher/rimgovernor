package sustained

import (
	"context"
	"errors"
	"fmt"
	"regexp"
	"sort"
	"strings"

	na "github.com/davidarcher/RimGovernor/go/internal/nativeaccept"
	"github.com/davidarcher/RimGovernor/go/internal/nativeaccept/cases"
)

// colonyFoodZoneLimit is the most stockpile zones allowing human food a
// colony window may end with (#1581): the food stockpile, the raw-food
// freezer and the larder, with room for the further warehouse a full store
// asks for. A playtest ended its second day with about twenty.
const colonyFoodZoneLimit = 6

// startingSupplyDefs are the tribal start stacks the scenario forbids. Only
// Pemmican gates: wood stacks outside the base stay forbidden on purpose
// (ManageSupplySafety, outside_base:insufficient_defense), so wood is only
// reported (#1581).
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
	churn, err := auditZoneChurn(s, rows)
	if err != nil {
		return err
	}
	report["stockpile_zones"] = map[string]any{"stockpiles": stockpiles, "food": food, "limit": colonyFoodZoneLimit, "rows": rows, "starting_supplies": stocks, "per_kind": churn.perKind, "deletes": churn.deletes}
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
// store, and a delete only when the store's purpose is gone (#2207).
type zoneChurn struct {
	// perKind counts the standing stockpile zones by label with its
	// numbering stripped. A further warehouse or graveyard is a second zone
	// of a kind by design, so it is reported, not gated.
	perKind map[string]int
	// deletes counts the admitted zone deletes by the role they name.
	deletes map[string]int
	// flagged lists the roles deleted in the window whose kind still has a
	// standing zone: the delete did not follow a retired purpose.
	flagged []string
}

var (
	zoneLabelNumber = regexp.MustCompile(`[\s_:-]*\d+$`)
	zoneDeleteRole  = regexp.MustCompile(`\(([^)]+)\): role retired`)
)

// auditZoneChurn reads the flight recorder's admitted stockpile deletes
// (layout_edit, family stockpile, kind delete: MaintainStockpiles deletes only
// a zone whose role's purpose the department declared gone) against the
// standing zones. It is post-hoc like the rest of the audit: a recorder that
// cannot be read leaves the counts empty.
func auditZoneChurn(s cases.Session, rows []map[string]any) (zoneChurn, error) {
	churn := zoneChurn{perKind: map[string]int{}, deletes: map[string]int{}}
	for _, row := range rows {
		kind := strings.ToLower(zoneLabelNumber.ReplaceAllString(na.AsString(row["label"]), ""))
		churn.perKind[kind]++
	}
	flight, err := na.ReadFlight(na.FlightRecorderPath(s.Config().Output))
	if err != nil {
		return churn, nil
	}
	for _, row := range flight {
		if row.Kind != "layout_edit" || na.AsString(row.Payload["verdict"]) != "admitted" {
			continue
		}
		attrs, _ := na.AsMap(row.Payload["attrs"])
		if na.AsString(attrs["family"]) != "stockpile" || na.AsString(attrs["kind"]) != "delete" {
			continue
		}
		match := zoneDeleteRole.FindStringSubmatch(na.AsString(attrs["detail"]))
		if match == nil {
			continue
		}
		role := match[1]
		churn.deletes[role]++
		prefix := strings.ToLower(strings.SplitN(role, ":", 2)[0])
		for kind := range churn.perKind {
			if prefix != "" && strings.Contains(kind, prefix) {
				churn.flagged = append(churn.flagged, role)
				break
			}
		}
	}
	sort.Strings(churn.flagged)
	return churn, nil
}
