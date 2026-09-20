package mood

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func TestBerserkVerdictRequiresNativeOwnedBedAndSurvival(t *testing.T) {
	for _, change := range []string{"valid", "deaths", "prisoners", "mental", "downed", "delivered", "bed", "ownedBed", "alive", "colonist", "colonists", "missing-deaths", "missing-prisoners"} {
		t.Run(change, func(t *testing.T) {
			a := map[string]any{"colonists": float64(8), "alive": true, "colonist": true, "mental": false, "downed": true, "delivered": true, "deaths": []any{}, "prisoners": []any{}, "bed": "bed", "ownedBed": "bed"}
			switch change {
			case "missing-deaths":
				delete(a, "deaths")
			case "missing-prisoners":
				delete(a, "prisoners")
			case "deaths", "prisoners":
				a[change] = []any{"original-colonist"}
			case "mental":
				a[change] = true
			case "bed", "ownedBed":
				a[change] = "another-bed"
			case "colonists":
				a[change] = float64(7)
			case "valid":
			default:
				a[change] = false
			}
			if err := berserkOutcome(a, "bed"); (err == nil) != (change == "valid") {
				t.Fatalf("%s: %v", change, err)
			}
		})
	}
}

func TestBerserkDispatchAuditsWorkerAndTargetAndAllowsRescueOnlyAfterDowning(t *testing.T) {
	order := func(kind, pawn, target string) map[string]any {
		return map[string]any{"pawnTargetOrder": map[string]any{"kind": "PAWN_ORDER_KIND_" + kind, "pawn": map[string]any{"entityId": pawn}, "target": map[string]any{"entityId": target}}}
	}
	for _, tc := range []struct {
		name, kind, pawn, target string
		active, want             bool
	}{
		{"squad", "SUBDUE", "squad", "broken", true, true},
		{"wrong-squad", "SUBDUE", "far", "broken", true, false},
		{"near-worker", "REPAIR", "near", "far-wall", true, false},
		{"near-target", "REPAIR", "far", "near-wall", true, false},
		{"safe-work", "REPAIR", "far", "far-wall", true, true},
		{"unknown-work", "REPAIR", "unknown", "unknown", true, false},
		{"unknown-worker", "REPAIR", "unknown", "far-wall", true, false},
		{"unknown-target", "REPAIR", "far", "unknown", true, false},
		{"early-rescue", "RESCUE", "far", "broken", true, false},
		{"rescue", "RESCUE", "far", "broken", false, true},
		{"capture", "CAPTURE", "far", "broken", false, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			a := berserkDispatch{target: "broken", squad: map[string]bool{"squad": true}, active: tc.active, positions: map[string]domain.Cell{"broken": {X: 10, Z: 10}, "near": {X: 18, Z: 10}, "near-wall": {X: 10, Z: 11}, "far": {X: 30, Z: 30}, "far-wall": {X: 31, Z: 30}}}
			if err := a.dispatch(order(tc.kind, tc.pawn, tc.target)); (err == nil) != tc.want {
				t.Fatalf("allowed=%v: %v", tc.want, err)
			}
		})
	}
}
