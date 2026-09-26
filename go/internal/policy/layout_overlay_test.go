package policy

import (
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/domain"
)

func TestLayoutOverlayOutlinesModulesAndLabelsThem(t *testing.T) {
	survey := surveyMap(160, 160, func(x, z int32) SurveyCell { return SurveyCell{Walkable: true, Fertility: 1} })
	plan, ok := DeriveMasterPlan(survey, 3).Value()
	if !ok {
		t.Fatal("no plan")
	}
	o := plan.Overlay(survey.Bounds)
	if len(o.Rooms) == 0 || o.Layers[0].Label != "aisles" || o.Layers[0].Color != planGray {
		t.Fatalf("rooms %d first layer %+v", len(o.Rooms), o.Layers[0].Label)
	}
	for _, r := range o.Layers[0].Rects {
		if r.Height != 1 || r.Width < 1 {
			t.Fatalf("aisle run %+v", r)
		}
	}
	drawn := 0
	for _, m := range plan.Modules {
		if m.Role != ModuleUnusable {
			drawn++
		}
	}
	if len(o.Layers) != drawn+1 || len(o.Labels) != drawn {
		t.Fatalf("layers %d labels %d modules %d", len(o.Layers), len(o.Labels), drawn)
	}
	// A module layer is its 13x13 ring: 48 cells, none inside.
	plaza := plan.Plaza()
	for _, l := range o.Layers[1:] {
		if l.Label != "plaza" {
			continue
		}
		cells := int32(0)
		for _, r := range l.Rects {
			cells += r.Width * r.Height
			if r.X > plaza.X && r.X+r.Width < plaza.X+plaza.Width && r.Z > plaza.Z && r.Z+r.Height < plaza.Z+plaza.Height {
				t.Fatalf("interior rect %+v", r)
			}
		}
		if cells != 2*plaza.Width+2*plaza.Height-4 || l.Color != planAmber {
			t.Fatalf("plaza ring %d cells color %s", cells, l.Color)
		}
	}
	centre := domain.Cell{X: plaza.X + plaza.Width/2, Z: plaza.Z + plaza.Height/2}
	found := false
	for _, l := range o.Labels {
		found = found || (l.Text == "plaza" && l.Cell == centre)
	}
	if !found {
		t.Fatalf("plaza label missing at %v", centre)
	}
}

func TestLayoutOverlayClipsToTheMap(t *testing.T) {
	if r, ok := clip(Rectangle{X: -3, Z: 5, Width: 10, Height: 1}, Bounds{Width: 4, Height: 10}); !ok || r != (Rectangle{X: 0, Z: 5, Width: 4, Height: 1}) {
		t.Fatalf("%+v %v", r, ok)
	}
	if _, ok := clip(Rectangle{X: 20, Z: 0, Width: 2, Height: 2}, Bounds{Width: 4, Height: 4}); ok {
		t.Fatal("outside rect kept")
	}
	runs := rowRuns([]domain.Cell{{X: 1, Z: 0}, {X: 2, Z: 0}, {X: 4, Z: 0}, {X: 0, Z: 2}})
	if len(runs) != 3 || runs[0] != (Rectangle{X: 1, Z: 0, Width: 2, Height: 1}) || runs[2] != (Rectangle{X: 0, Z: 2, Width: 1, Height: 1}) {
		t.Fatalf("%+v", runs)
	}
}
