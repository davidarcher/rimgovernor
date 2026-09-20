package production

import (
	"context"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/store"
)

func TestMaterialOutcomeRequiresNativeProduction(t *testing.T) {
	before := map[string]any{"steel": 150.0, "components": 0.0, "deepSteel": 2700.0}
	for _, tc := range []struct {
		name, scenario string
		after          map[string]any
		pass           bool
	}{
		{"drill receipt only", "deepdrill", map[string]any{"steel": 150.0, "deepSteel": 2700.0, "drillsOnLump": 1.0}, false},
		{"unrelated steel", "deepdrill", map[string]any{"steel": 200.0, "deepSteel": 2700.0, "drillsOnLump": 1.0}, false},
		{"missing depletion", "deepdrill", map[string]any{"steel": 200.0, "deepSteel": nil, "drillsOnLump": 1.0}, false},
		{"drilled steel", "deepdrill", map[string]any{"components": 0.0, "steel": 200.0, "deepSteel": 2500.0, "drillsOnLump": 1.0}, true},
		{"bill receipt only", "components", map[string]any{"steel": 150.0, "components": 0.0, "componentBills": 1.0}, false},
		{"unfunded increase", "components", map[string]any{"steel": 150.0, "components": 1.0, "componentBills": 1.0}, false},
		{"fabricated component", "components", map[string]any{"deepSteel": 2700.0, "steel": 138.0, "components": 1.0, "componentBills": 1.0}, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			for key, value := range before {
				if _, present := tc.after[key]; !present {
					tc.after[key] = value
				}
			}
			if err := materialOutcome(before, tc.after, tc.scenario); (err == nil) != tc.pass {
				t.Fatalf("pass=%v, err=%v", tc.pass, err)
			}
		})
	}
}

func TestMaterialHistoryIsCancelledBeforeService(t *testing.T) {
	ctx := context.Background()
	s, err := store.Open(ctx, filepath.Join(t.TempDir(), "service.sqlite"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	if err := seedMaterialHistory(ctx, s, map[string]any{"colonyId": "test", "loadToken": "load", "mapId": 0.0}, 120000); err != nil {
		t.Fatal(err)
	}
	p, err := s.LoadPlan(ctx, "materials-history")
	if err != nil {
		t.Fatal(err)
	}
	if v := p.Progress[0].View(); v.Stage != "cancelled" || v.Unresolved {
		t.Fatalf("history could execute: %+v", v)
	}
}
