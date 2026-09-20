package clearance

import "testing"

func TestShrineBreachRequiresNativeOutcomes(t *testing.T) {
	for _, fault := range []string{"", "guard_alive", "colonist_dead", "wall_present", "salvage_present", "missing_casket", "opened", "unclaimed", "open_designation", "duplicate"} {
		t.Run(fault, func(t *testing.T) {
			empty := map[string]any{"id": "empty", "playerOwned": true, "hasContents": false}
			filled := map[string]any{"id": "filled", "hasContents": true}
			audit := map[string]any{"guardDead": true, "colonistsDead": 0, "caskets": []any{empty, filled}}
			switch fault {
			case "guard_alive":
				audit["guardDead"] = false
			case "colonist_dead":
				audit["colonistsDead"] = 1
			case "wall_present":
				audit["breachPresent"] = true
			case "salvage_present":
				audit["salvagePresent"] = true
			case "missing_casket":
				audit["caskets"] = []any{empty}
			case "opened":
				filled["hasContents"] = false
			case "unclaimed":
				empty["playerOwned"] = false
			case "open_designation":
				filled["designated"] = true
			case "duplicate":
				audit["caskets"] = []any{empty, empty}
			}
			if err := checkShrineBreach(audit, []string{"empty", "filled"}, true); (err != nil) != (fault != "") {
				t.Fatalf("fault %s: %v", fault, err)
			}
		})
	}
}
