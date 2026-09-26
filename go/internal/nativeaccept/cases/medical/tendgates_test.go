package medical

import "testing"

func tendGatesRow(id string, reachable ...any) map[string]any {
	return map[string]any{"pawn": map[string]any{"id": id}, "dead": false, "downed": false,
		"tendDoctor": map[string]any{"controlEligible": true, "spawned": true, "hasDrafter": true,
			"capacitiesOk": true, "workTypeDisabled": false, "reachablePawnIds": reachable}}
}

func TestTendGatesRequireEveryDoctorGate(t *testing.T) {
	observed := map[string]any{"pawns": []any{tendGatesRow("doctor", "patient"), tendGatesRow("patient", "doctor")}}
	if err := tendGatesObserve(observed); err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct {
		name   string
		change func(map[string]any)
	}{
		{"detail absent", func(row map[string]any) { delete(row, "tendDoctor") }},
		{"control eligibility unknown", func(row map[string]any) {
			delete(row["tendDoctor"].(map[string]any), "controlEligible")
		}},
		{"capacities unknown", func(row map[string]any) {
			delete(row["tendDoctor"].(map[string]any), "capacitiesOk")
		}},
		{"work type unknown", func(row map[string]any) {
			delete(row["tendDoctor"].(map[string]any), "workTypeDisabled")
		}},
		{"eligible contradicts the row", func(row map[string]any) {
			row["tendDoctor"].(map[string]any)["controlEligible"] = false
		}},
		{"reaches itself", func(row map[string]any) {
			row["tendDoctor"].(map[string]any)["reachablePawnIds"] = []any{"doctor", "patient"}
		}},
		{"asymmetric reachability", func(row map[string]any) {
			row["tendDoctor"].(map[string]any)["reachablePawnIds"] = []any{}
		}},
	} {
		t.Run(c.name, func(t *testing.T) {
			rows := []any{tendGatesRow("doctor", "patient"), tendGatesRow("patient", "doctor")}
			c.change(rows[0].(map[string]any))
			if err := tendGatesObserve(map[string]any{"pawns": rows}); err == nil {
				t.Fatal("accepted an unobserved or contradictory tend gate")
			}
		})
	}
}
