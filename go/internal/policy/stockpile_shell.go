package policy

import "fmt"

// StockpileShell raises a planned storage room the stockpile roles belong
// in (#1774): the armory or wardrobe layout planned on gear demand. Role
// names the room's layout module; the planner shells it like any planned room.
const StockpileShell StockpileEditKind = "shell"

// stockpileShellEdits proposes one shell edit for each planned room in
// Shells. A shell triggers no hauling.
func stockpileShellEdits(r StockpileRequest) []StockpileEdit {
	var out []StockpileEdit
	for _, module := range r.Shells {
		out = append(out, StockpileEdit{Kind: StockpileShell, Role: string(module),
			Explanation: fmt.Sprintf("planned %s room not standing: raise its shell", module)})
	}
	return out
}
