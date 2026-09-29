package buildingruntime

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/policy"
	"github.com/davidarcher/RimGovernor/go/internal/snapshot"
)

// Replaces the native layout/grid case's field half (#603, #982): recorded
// from `acceptance run layout/grid` at 1caf938d9 on the tribal baseline
// with Stonecutting finished and a fixture hut standing, the first field
// step's own read (tick 15) and the review it planned under. The case's
// row-partner assertion (#608) is gone with the colony grid (#1175): fields
// now fill the layout plan's field blocks (#1223, #1227), which this test
// asserts.
func TestLayoutGridFieldFillsPlanFieldBlocks(t *testing.T) {
	r, err := snapshot.Load("testdata/layout-grid-review.json.gz")
	if err != nil {
		t.Fatal(err)
	}
	step := loadStep(t, "layout-grid-field", policy.EnsureFoodSupply)
	projection := step.Projection
	fields, ok := layoutFieldCells(projection)
	if !ok || len(fields) == 0 {
		t.Fatal("no layout plan field blocks in the step read")
	}
	reserve := r.Policy.Seasonal(projection.Facts.Calendar, projection.Facts.DisasterConditions).FoodTargetDays
	request, _ := fieldSiteRequest(projection, nil, reserve)
	selection, known := policy.PlanSiteType(request)
	if !known || len(selection.Candidates) == 0 {
		t.Fatal("no site selection", selection)
	}
	var planned bool
	for _, c := range selection.Candidates {
		if c.Kind != policy.SiteOutdoor || len(c.Buildings) != 0 || c.Cells == 0 {
			continue
		}
		edit, reason, ok := planFieldBlock(projection, request.Field.Site.Anchor, fieldBlockOptions(c, selection.Candidates), nil)
		if !ok {
			t.Fatalf("outdoor %s refused: %s", c.Crop.Name, reason)
		}
		if len(edit.Cells) == 0 {
			t.Fatal("empty field block edit", edit)
		}
		for _, cell := range edit.Cells {
			if !fields[cell] {
				t.Fatalf("field cell %v lies outside the plan field blocks", cell)
			}
		}
		planned = true
		break
	}
	if !planned {
		t.Fatal("no outdoor field candidate", selection.Explain())
	}
}
