package archgate

import (
	"path/filepath"
	"strings"
	"testing"
)

// Every caller of domain.NewBuildingAction states a ladder tier (#2525).
func TestBuildingActionCallersStateTheirTier(t *testing.T) {
	for _, m := range BuildingActionTiers(filepath.Join("..", "..")) {
		t.Errorf("construction tier: %s", m)
	}
}

func TestBuildingActionTiersFailOnViolation(t *testing.T) {
	root := write(t, map[string]string{
		"internal/buildingruntime/a.go": "package buildingruntime\n" +
			"func ok() { domain.NewBuildingAction(id, b, domain.TierSustain); domain.NewBuildingAction(id, b, policy.RoomTier(role)); domain.NewBuildingAction(id, b, policy.PlannerTier(c, p)) }\n" +
			"func omitted() { domain.NewBuildingAction(id, b) }\n" +
			"func variable(tier domain.ConstructionTier) { domain.NewBuildingAction(id, b, tier) }\n" +
			"func computed() { domain.NewBuildingAction(id, b, domain.ConstructionTier(n)) }\n" +
			"func other() { domain.NewBuildingAction(id, b, other.TierSustain) }\n",
		"internal/domain/plan.go":       "package domain\nfunc NewPlan() { NewBuildingAction(id, b, tier) }\nfunc other() { NewBuildingAction(id, b, TierExpand); NewBuildingAction(id, b, tier) }\n",
		"internal/buildingruntime/b.go": "package buildingruntime\nfunc unrelated() { other.NewBuildingAction(id, b); NewBuildingAction(id, b) }\n",
	})
	got := BuildingActionTiers(root)
	want := []string{
		"internal/buildingruntime/a.go|omitted|no tier argument",
		"internal/buildingruntime/a.go|variable|tier is not a ladder constant or policy mapping: tier",
		"internal/buildingruntime/a.go|computed|tier is not a ladder constant or policy mapping: domain.ConstructionTier(n)",
		"internal/buildingruntime/a.go|other|tier is not a ladder constant or policy mapping: other.TierSustain",
		"internal/domain/plan.go|other|tier is not a ladder constant or policy mapping: tier",
	}
	for _, w := range want {
		if !has(got, w) {
			t.Errorf("missing %q in %v", w, got)
		}
	}
	if len(got) != len(want) {
		t.Errorf("got %v, want exactly %v", got, want)
	}
	for _, g := range got {
		if strings.Contains(g, "|ok|") || strings.Contains(g, "NewPlan") || strings.Contains(g, "unrelated") {
			t.Errorf("flagged a compliant or unrelated caller: %s", g)
		}
	}
}
