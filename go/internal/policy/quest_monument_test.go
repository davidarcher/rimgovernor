package policy

import (
	"github.com/davidarcher/RimGovernor/go/internal/domain"
	"testing"
)

func monumentOffers(m QuestMonument) domain.Fact[[]JoinerOffer] {
	return domain.Known([]JoinerOffer{{Quest: "quest", State: "Ongoing", Objectives: []QuestObjective{{Monument: domain.Known(m)}}}})
}

func TestMonumentInstallationProtectsWholeSketch(t *testing.T) {
	m := QuestMonument{Marker: "marker", Def: "MonumentMarker", Map: 1, Packed: domain.Known(true), InstallCells: []domain.Cell{{X: 5, Z: 5}, {X: 20, Z: 20}}, Pieces: []QuestMonumentPiece{{Offset: domain.Cell{}, Footprint: []domain.Cell{{X: 0, Z: 0}, {X: 1, Z: 0}}}}}
	step := SelectQuestMonument(monumentOffers(m), 1, nil, []Rectangle{{X: 6, Z: 5, Width: 1, Height: 1}})
	if step.Kind != "install" || step.Move.Cell() != (domain.Cell{X: 20, Z: 20}) {
		t.Fatalf("%+v", step)
	}
}

func TestMonumentProgressAndDeterministicMaterials(t *testing.T) {
	piece := QuestMonumentPiece{Def: "Wall", Rotation: domain.North, Offset: domain.Cell{X: 1, Z: 2}, Built: domain.Known(false), Queued: domain.Known(false), Allowed: domain.Known(true), AllowedStuffs: []string{"WoodLog", "Steel"}}
	m := QuestMonument{Marker: "marker", Def: "MonumentMarker", Map: 1, Installed: domain.Known(true), Cell: domain.Cell{X: 20, Z: 20}, AllDone: domain.Known(false), Pieces: []QuestMonumentPiece{piece}}
	step := SelectQuestMonument(monumentOffers(m), 1, map[Resource]int64{"Steel": 10, "WoodLog": 10}, nil)
	if step.Kind != "build" || step.Build.Stuff() != "Steel" {
		t.Fatalf("%+v", step)
	}
	m.Pieces[0].Queued = domain.Known(true)
	if step := SelectQuestMonument(monumentOffers(m), 1, nil, nil); step.Kind != "wait" {
		t.Fatalf("queued is not completion: %+v", step)
	}
	m.AllDone = domain.Known(true)
	if step := SelectQuestMonument(monumentOffers(m), 1, nil, nil); step.Kind != "protect" {
		t.Fatalf("%+v", step)
	}
	m.DisallowedBuilding = "intruder"
	if step := SelectQuestMonument(monumentOffers(m), 1, nil, nil); step.Reason != "disallowed_building" {
		t.Fatalf("%+v", step)
	}
}

func TestMonumentHaulsOnlyNativeEligibleResources(t *testing.T) {
	m := QuestMonument{Marker: "marker", Def: "MonumentMarker", Map: 1, Installed: domain.Known(true), Resources: []QuestMonumentResource{{ID: "steel", Def: "Steel", InStorage: domain.Known(false), Haulers: []domain.PawnID{"b", "a"}}}}
	step := SelectQuestMonument(monumentOffers(m), 1, nil, nil)
	if step.Kind != "haul" || step.Haul.Pawn() != "a" {
		t.Fatalf("%+v", step)
	}
	if step := SelectQuestMonument(monumentOffers(m), 2, nil, nil); step.Kind != "" {
		t.Fatalf("wrong map: %+v", step)
	}
}
