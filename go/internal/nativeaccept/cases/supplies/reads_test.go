package supplies

import "testing"

// suppliesComplete is a complete supplies census page numbered number.
func suppliesComplete(number int) map[string]any {
	return map[string]any{"page": map[string]any{"complete": true}, "matched": itoaSupplies(number), "returned": itoaSupplies(number), "unreadable": "0"}
}

func itoaSupplies(n int) string {
	if n == 0 {
		return "0"
	}
	digits := ""
	for n > 0 {
		digits = string(rune('0'+n%10)) + digits
		n /= 10
	}
	return digits
}

// suppliesSamples returns the complete and truncated sample pages.
func suppliesSamples() (map[string]any, map[string]any) {
	legacy := map[string]any{
		"defName": "WoodLog", "total": 5.0, "stacks": 2.0, "ours": 2.0, "oursUnforbidden": 2.0, "forbidden": 0.0,
		"otherFaction": 3.0, "fogged": 0.0, "reserved": 0.0, "inStockpile": 2.0, "inHomeArea": 5.0,
		"carried": 3.0, "inContainer": 0.0, "traderStock": 3.0,
	}
	row := map[string]any{
		"stacks": "2", "ours": "2", "oursUnforbidden": "2", "forbidden": "0", "otherFaction": "3",
		"fogged": "0", "reserved": "0", "inStockpile": "2", "inHomeArea": "5",
		"carried": "3", "inContainer": "0", "traderStock": "3",
		"definition": map[string]any{"defName": "WoodLog"}, "units": "5", "spawned": "2", "playerFaction": "0",
		"items": []any{
			map[string]any{"id": "wood1", "mapId": 0.0, "snapshot": map[string]any{"context": map[string]any{}, "entityId": "wood1", "token": "tok1"}},
			map[string]any{"id": "wood2", "mapId": 0.0, "snapshot": map[string]any{"context": map[string]any{}, "entityId": "wood2", "token": "tok2"}},
		},
		"holders":             []any{map[string]any{"holder": map[string]any{"id": "muffalo1"}, "units": "3"}},
		"itemsCompleteness":   suppliesComplete(2),
		"holdersCompleteness": suppliesComplete(1),
		"corpsesCompleteness": suppliesComplete(0),
		"issues":              []any{},
	}
	return row, legacy
}

func copySuppliesAny(m map[string]any) map[string]any {
	out := map[string]any{}
	for k, v := range m {
		out[k] = v
	}
	return out
}

func TestCheckStockRequiresActualQuantitiesAndCompleteInstances(t *testing.T) {
	row, legacy := suppliesSamples()
	if err := checkStock(row, legacy, map[string]any{"mapId": 0.0}, true); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	cases := []struct {
		field string
		value any
	}{
		{"units", "0"}, {"carried", "0"}, {"ours", "5"}, {"items", []any{}},
		{"holders", []any{}}, {"itemsCompleteness", suppliesComplete(0)},
	}
	for _, c := range cases {
		t.Run(c.field, func(t *testing.T) {
			changed := copySuppliesAny(row)
			changed[c.field] = c.value
			if err := checkStock(changed, legacy, map[string]any{"mapId": 0.0}, true); err == nil {
				t.Fatalf("expected an error for changed field %q", c.field)
			}
		})
	}
	missing := copySuppliesAny(row)
	delete(missing, "carried")
	if err := checkStock(missing, legacy, map[string]any{"mapId": 0.0}, true); err == nil {
		t.Fatal("expected an error for a missing carried field")
	}
}

func TestCheckStockExcludedHeldScopeCannotClaimKnownEmpty(t *testing.T) {
	row, legacy := suppliesSamples()
	for _, key := range []string{"carried", "inContainer", "traderStock"} {
		legacy[key] = 0.0
		delete(row, key)
	}
	legacy["total"] = 2.0
	legacy["stacks"] = 1.0
	legacy["otherFaction"] = 0.0
	legacy["inHomeArea"] = 2.0
	row["units"] = "2"
	row["stacks"] = "1"
	row["otherFaction"] = "0"
	row["inHomeArea"] = "2"
	items := row["items"].([]any)
	row["items"] = items[:1]
	row["holders"] = []any{}
	row["itemsCompleteness"] = suppliesComplete(1)
	row["holdersCompleteness"] = map[string]any{"page": map[string]any{"complete": false}}
	issues := []any{}
	for _, field := range []string{"carried", "in_container", "trader_stock"} {
		issues = append(issues, map[string]any{"field": field, "unavailable": map[string]any{"reason": "UNAVAILABLE_REASON_NOT_REQUESTED"}})
	}
	row["issues"] = issues
	if err := checkStock(row, legacy, map[string]any{"mapId": 0.0}, false); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	row["carried"] = "0"
	if err := checkStock(row, legacy, map[string]any{"mapId": 0.0}, false); err == nil {
		t.Fatal("expected an error: carried must not be present at all when excluded from scope")
	}
}

func TestCountRejectsMissingOrNoncanonicalQuantities(t *testing.T) {
	for _, invalid := range []any{nil, false, 0.0, -1.0, "-1", "01", "1.0", "١", "9223372036854775808"} {
		t.Run("", func(t *testing.T) {
			if _, err := count(invalid); err == nil {
				t.Fatalf("expected an error for invalid quantity %#v", invalid)
			}
		})
	}
}
