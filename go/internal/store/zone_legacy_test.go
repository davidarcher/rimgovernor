package store

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

func allowListZone(priority domain.StockpilePriority, allow []string, cells []domain.Cell) (domain.ZoneCreate, error) {
	f, err := domain.AllowOnlyFilter(allow)
	if err != nil {
		return domain.ZoneCreate{}, err
	}
	return domain.NewFilteredStockpileZone(f, priority, cells)
}

// Rows written before #932 name a stockpile by preset; they still load, as
// the filter the preset stood for.
func TestLegacyPresetZoneRowsLoad(t *testing.T) {
	cells := []domain.Cell{{X: 4, Z: 6}, {X: 5, Z: 6}}
	allow, _ := allowListZone(domain.ImportantPriority, []string{"MealFine", "MealSimple"}, cells)
	food, _ := domain.NewFilteredStockpileZone(domain.FoodFilter(), domain.ImportantPriority, cells)
	general, _ := domain.NewFilteredStockpileZone(domain.GeneralFilter(), domain.NormalPriority, cells)
	larder, _ := domain.NewFilteredStockpileZone(domain.CorpseLarderFilter(), domain.ImportantPriority, cells)
	for _, c := range []struct {
		row  zonePayload
		want domain.ZoneCreate
	}{
		{zonePayload{Kind: domain.StockpileZone, Preset: "nothing", Priority: domain.ImportantPriority, Cells: cells, Allow: []string{"MealFine", "MealSimple"}}, allow},
		{zonePayload{Kind: domain.StockpileZone, Preset: "food", Priority: domain.ImportantPriority, Cells: cells}, food},
		{zonePayload{Kind: domain.StockpileZone, Preset: "general", Priority: domain.NormalPriority, Cells: cells}, general},
		{zonePayload{Kind: domain.StockpileZone, Preset: "corpse_larder", Priority: domain.ImportantPriority, Cells: cells}, larder},
	} {
		ctx := context.Background()
		s := open(t, memoryPath(t))
		r := foodStorageDeficitRoutineRequest()
		out := reviewRoutine(t, s, &r)
		g := routineGoal(t, out, policy.MaintainFoodStorage)
		action, _ := domain.NewZoneCreateAction("storage-plan-a", food)
		plan, _ := domain.NewPlan("storage-plan", 1, []domain.Action{action})
		if _, err := s.CommitGoalMethod(ctx, g.Goal.ID, g.Revision, "food-storage", plan); err != nil {
			t.Fatal(err)
		}
		data, _ := json.Marshal(c.row)
		if _, err := s.db.Exec("UPDATE actions SET zone_payload=? WHERE id='storage-plan-a'", data); err != nil {
			t.Fatal(err)
		}
		loaded, err := s.LoadPlan(ctx, "storage-plan")
		if err != nil {
			t.Fatal(c.row.Preset, err)
		}
		if got, _ := loaded.Spec.Actions()[0].ZoneCreate(); got != c.want {
			t.Fatal(c.row.Preset, got, c.want)
		}
	}
}
