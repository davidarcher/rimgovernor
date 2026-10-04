package buildingruntime

import (
	"fmt"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// The daily bound counts only firebreak methods inside the last game day.
func TestFirebreakAttemptsDailyBound(t *testing.T) {
	now := domain.Tick(10 * firebreakWindowTicks)
	var history []domain.Method
	for i := 0; i < maxFirebreakAttempts; i++ {
		history = append(history, domain.Method{Method: domain.MethodID(fmt.Sprintf("%s%d", firebreakPrefix, now-domain.Tick(i*1000)))})
	}
	if got := firebreakAttempts(history, now); got != maxFirebreakAttempts {
		t.Fatal(got)
	}
	history = append(history[:1], domain.Method{Method: domain.MethodID(fmt.Sprintf("%s%d", firebreakPrefix, now-firebreakWindowTicks))}, domain.Method{Method: "cut-other"})
	if got := firebreakAttempts(history, now); got != 1 {
		t.Fatal(got)
	}
}

func TestFirebreakActionsCutAndRuins(t *testing.T) {
	ruins := map[domain.Cell]domain.CoverClearance{}
	work := policy.FirebreakWork{Cut: []domain.Cell{{X: 3, Z: 1}, {X: 1, Z: 1}}}
	for i := int32(0); i < maxDefenseCoverBatch+2; i++ {
		cell := domain.Cell{X: 20 + i, Z: 5}
		clearance, err := domain.NewCoverClearance(fmt.Sprintf("Wall%d", i), "Wall", domain.CoverClearanceDeconstruct, cell)
		if err != nil {
			t.Fatal(err)
		}
		ruins[cell] = clearance
		work.Deconstruct = append(work.Deconstruct, cell)
	}
	got, err := firebreakActions("plan-1", work, ruins)
	if err != nil || got.cut != 2 || got.ruins != maxDefenseCoverBatch || len(got.actions) != 1+maxDefenseCoverBatch {
		t.Fatal(got, err)
	}
	cut, ok := got.actions[0].AreaPlantCut()
	if !ok || len(cut.Cells()) != 2 {
		t.Fatal(got.actions[0])
	}
}

func TestFirebreakGround(t *testing.T) {
	for _, tt := range []struct {
		cell bridge.DefenseCell
		want policy.FirebreakGround
	}{
		{bridge.DefenseCell{Terrain: "Soil"}, policy.FirebreakOpen},
		{bridge.DefenseCell{Terrain: "WaterShallow"}, policy.FirebreakWater},
		{bridge.DefenseCell{NaturalRock: true, EdificeDefName: "Granite"}, policy.FirebreakNaturalRock},
		{bridge.DefenseCell{Fogged: true}, policy.FirebreakNaturalRock},
		{bridge.DefenseCell{EdificeDefName: "Wall", EdificeStuff: "WoodLog", PlayerOwned: true}, policy.FirebreakPlayerEdifice},
		{bridge.DefenseCell{EdificeDefName: "Wall", EdificeStuff: "WoodLog"}, policy.FirebreakWoodenRuin},
		{bridge.DefenseCell{EdificeDefName: "Wall", EdificeStuff: "BlocksGranite"}, policy.FirebreakStoneRuin},
	} {
		if got := firebreakGround(tt.cell); got != tt.want {
			t.Error(tt.cell, got)
		}
	}
}
