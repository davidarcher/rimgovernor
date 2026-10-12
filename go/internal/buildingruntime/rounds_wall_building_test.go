package buildingruntime

import (
	"context"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/bridge"
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"github.com/davidarcher/RimGovernor/go/internal/observation"
	"github.com/davidarcher/RimGovernor/go/internal/policy"
)

// wallPreviewNative previews any def as legal on exactly its own cell.
type wallPreviewNative struct {
	observation.RoundsSource
	previewed []string
}

func (n *wallPreviewNative) PreviewBuilding(_ context.Context, action domain.Action, _ domain.GenerationSnapshot) (bridge.BuildingPreview, bridge.Result, error) {
	b, _ := action.Building()
	n.previewed = append(n.previewed, b.Definition())
	return bridge.BuildingPreview{Preview: policy.Preview{
		Action:        action,
		Footprint:     domain.Known([]domain.Cell{b.Cell()}),
		MadeFromStuff: domain.Known(false),
		CanPlace:      domain.Known(true),
		SafeToPlace:   domain.Known(true),
	}}, bridge.Result{}, nil
}

func TestWallBuildingPreviewTakesAnyDefAndRefusesUnresearched(t *testing.T) {
	t.Parallel()
	cell := domain.Cell{X: 3, Z: 2}
	snapshot := domain.GenerationSnapshot{Plan: "plan-vent"}
	run := func(vent domain.Fact[bool]) (*wallPreviewNative, []policy.Preview, Verdict) {
		n := &wallPreviewNative{}
		r := &RoundsBuildingPlanner{native: n}
		facts := observation.ColonyProjection{Definitions: []observation.PlanningDefinition{{Name: "Vent", Available: vent}}}
		previews, _, verdict, err := r.previewWallBuilding(context.Background(), snapshot, facts, nil, nil, "Vent", cell, domain.North, false)
		if err != nil {
			t.Fatal(err)
		}
		return n, previews, verdict
	}

	n, previews, verdict := run(domain.Known(true))
	if !verdict.IsZero() || len(previews) != 1 || len(n.previewed) != 1 || n.previewed[0] != "Vent" {
		t.Fatal(previews, verdict, n.previewed)
	}
	if b, _ := previews[0].Action.Building(); b.Definition() != "Vent" || b.Cell() != cell || b.Rotation() != domain.North {
		t.Fatal(b)
	}

	n, previews, verdict = run(domain.Known(false))
	if want := fieldUnavailable("Vent_research"); verdict != want || previews != nil || len(n.previewed) != 0 {
		t.Fatal(previews, verdict, n.previewed)
	}
}
